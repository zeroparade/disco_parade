#!/usr/bin/env python3
"""Build deterministic sharded ZERO PARADES voice packs for Hugging Face.

The output layout is:

  manifests/<pack>.json
  packs/<pack>/shards/<pack>-b000-p00.zip

By default the tool reads the installed BepInEx voice folders from --game-root.
ZIP entries use stable timestamps and sorted paths. Shard filenames are stable
by bucket and part so publishing updates overwrites the same remote paths.
Content hashes live in the manifest. Files are assigned to stable hash buckets
by dialogue id, so adding or changing a line normally only invalidates that
bucket's shard instead of shifting the rest of the pack.
"""

from __future__ import annotations

import argparse
import csv
import os
import hashlib
import json
import shutil
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable


DEFAULT_PACKS = {
    "male": ("male replacement voices", "BepInEx/voice-overrides"),
    "female": ("female replacement voices", "BepInEx/voice-overrides-female"),
    "extras": ("extra character voices", "BepInEx/voice-override-extras"),
    "narrator": ("narrator missing voices", "BepInEx/voice-override-narrator"),
}
VOICE_EXTENSIONS = {".wav", ".ogg"}
VOICE_SIDECAR_FILES = {"_silent-card-ids.txt"}
ZIP_DATE_TIME = (1980, 1, 1, 0, 0, 0)
DEFAULT_WORKERS = min(8, max(1, os.cpu_count() or 4))
DEFAULT_REMOTE_COMPARE_BASE_URL = "https://huggingface.co/datasets/zeroparade/dead_disco/resolve/main"
SILENT_ONLY_PACKS = {"extras", "narrator"}


@dataclass(frozen=True)
class VoiceFile:
    source: Path
    relpath: str
    dialogue_id: str
    size: int
    sha256: str


@dataclass(frozen=True)
class BuildResult:
    manifest: dict[str, object]
    reused_shards: int
    written_shards: int
    removed_stale_shards: int
    changed_paths: list[str]


@dataclass(frozen=True)
class VoiceFileCandidate:
    source: Path
    relpath: str
    dialogue_id: str
    size: int


@dataclass(frozen=True)
class ShardPlan:
    index: int
    bucket: int
    part: int
    files: list[VoiceFile]
    fingerprint: str
    name: str
    rel_shard_path: str
    zip_path: Path
    old_shard: dict[str, object] | None
    base_url: str


@dataclass(frozen=True)
class ShardBuild:
    index: int
    record: dict[str, object]
    reused: bool
    written: bool


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def sha256_text(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def stable_json(data: object) -> str:
    return json.dumps(data, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def write_text_if_changed(path: Path, text: str) -> bool:
    if path.exists():
        try:
            if path.read_text(encoding="utf-8") == text:
                return False
        except OSError:
            pass
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")
    return True


def read_update_message(path: Path | None) -> str:
    if path is None or not path.exists() or not path.is_file():
        return ""
    try:
        text = path.read_text(encoding="utf-8", errors="replace").replace("\r\n", "\n").replace("\r", "\n").strip()
    except OSError:
        return ""
    return text[:2000].strip()


def join_url(base_url: str, relpath: str) -> str:
    return f"{base_url.rstrip('/')}/{relpath.lstrip('/')}"


def add_cache_buster(url: str) -> str:
    parsed = urllib.parse.urlsplit(url)
    query = urllib.parse.parse_qsl(parsed.query, keep_blank_values=True)
    query.append(("zpbuild", f"{int(time.time())}-{sha256_text(url)[:12]}"))
    return urllib.parse.urlunsplit(
        (parsed.scheme, parsed.netloc, parsed.path, urllib.parse.urlencode(query), parsed.fragment)
    )


def load_remote_json(url: str) -> dict[str, object] | None:
    request = urllib.request.Request(
        add_cache_buster(url),
        headers={
            "User-Agent": "ZeroParadesVoicePackBuilder/1.0",
            "Cache-Control": "no-cache",
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return json.loads(response.read().decode("utf-8-sig"))
    except (OSError, urllib.error.URLError, urllib.error.HTTPError, json.JSONDecodeError):
        return None


def parse_pack_message(spec: str) -> tuple[str, str]:
    parts = spec.split("=", 1)
    if len(parts) != 2 or not parts[0].strip():
        raise argparse.ArgumentTypeError("message spec must be pack=message, for example male=Fixed 30 lines")
    return parts[0].strip(), parts[1].strip()


def clamp_workers(workers: int) -> int:
    return max(1, min(32, workers))


def build_voice_file(candidate: VoiceFileCandidate) -> VoiceFile:
    return VoiceFile(
        source=candidate.source,
        relpath=candidate.relpath,
        dialogue_id=candidate.dialogue_id,
        size=candidate.size,
        sha256=sha256_file(candidate.source),
    )


def collect_voice_files(root: Path, workers: int) -> list[VoiceFile]:
    candidates: list[VoiceFileCandidate] = []
    if not root.exists():
        return []

    for path in sorted(root.rglob("*"), key=lambda p: p.as_posix().lower()):
        if not path.is_file():
            continue
        is_voice_file = path.suffix.lower() in VOICE_EXTENSIONS
        is_sidecar_file = path.name.lower() in VOICE_SIDECAR_FILES
        if not is_voice_file and not is_sidecar_file:
            continue
        relpath = path.relative_to(root).as_posix()
        candidates.append(
            VoiceFileCandidate(
                source=path,
                relpath=relpath,
                dialogue_id=path.stem,
                size=path.stat().st_size,
            )
        )

    if workers <= 1 or len(candidates) <= 1:
        return [build_voice_file(candidate) for candidate in candidates]

    with ThreadPoolExecutor(max_workers=workers) as executor:
        return list(executor.map(build_voice_file, candidates))


def read_silent_card_ids(root: Path) -> tuple[set[str], list[str]]:
    path = root / "_silent-card-ids.txt"
    if not path.exists():
        return set(), [f"missing required sidecar: {path}"]

    ids: list[str] = []
    errors: list[str] = []
    for line_number, raw in enumerate(path.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if any(char in line for char in "/\\:"):
            errors.append(f"{path}:{line_number}: invalid card id '{line}'")
            continue
        ids.append(line)

    seen: set[str] = set()
    duplicates = sorted({item for item in ids if item in seen or seen.add(item)})
    if duplicates:
        sample = ", ".join(duplicates[:20])
        errors.append(f"{path}: duplicate silent IDs ({len(duplicates)}): {sample}")
    return set(ids), errors


def find_default_official_vo_index(game_root: Path) -> Path | None:
    scratch = game_root / ".spore-code" / "scratch"
    candidates: list[Path] = []
    if scratch.exists():
        patch_candidates = []
        for patch_dir in scratch.glob("patch_compare_*"):
            for rel in (
                "new_all_bnk_vo_dialogue_decoded/all_bnk_vo_streams.csv",
                "new_all_bnk_vo_dialogue/all_bnk_vo_streams.csv",
            ):
                path = patch_dir / rel
                if path.exists():
                    patch_candidates.append(path)
        candidates.extend(sorted(patch_candidates, key=lambda item: item.stat().st_mtime, reverse=True))
        candidates.append(scratch / "voice_link_index" / "all_bnk_vo_dialogue" / "all_bnk_vo_streams.csv")
    for candidate in candidates:
        if candidate.exists():
            return candidate
    return None


def load_official_vo_ids(path: Path | None) -> set[str]:
    if path is None or not path.exists():
        return set()
    ids: set[str] = set()
    with path.open("r", encoding="utf-8-sig", errors="replace", newline="") as handle:
        for row in csv.DictReader(handle):
            card_id = str(row.get("card_id") or "").strip()
            if card_id:
                ids.add(card_id)
    return ids


def load_card_ids_from_csv(path: Path) -> set[str]:
    if not path.exists():
        return set()
    ids: set[str] = set()
    with path.open("r", encoding="utf-8-sig", errors="replace", newline="") as handle:
        for row in csv.DictReader(handle):
            card_id = str(row.get("card_id") or "").strip()
            if card_id:
                ids.add(card_id)
    return ids


def patch_compare_root_for_index(path: Path | None) -> Path | None:
    if path is None:
        return None
    for parent in [path, *path.parents]:
        if parent.name.startswith("patch_compare_"):
            return parent
    return None


def load_invalid_silent_ids(game_root: Path, official_vo_index: Path | None, official_vo_ids: set[str]) -> tuple[set[str], list[str]]:
    invalid = set(official_vo_ids)
    labels = []
    if official_vo_ids:
        labels.append(f"official VO index ({len(official_vo_ids)})")

    patch_root = patch_compare_root_for_index(official_vo_index)
    if patch_root is None:
        scratch = game_root / ".spore-code" / "scratch"
        patch_dirs = sorted(scratch.glob("patch_compare_*"), key=lambda item: item.stat().st_mtime, reverse=True) if scratch.exists() else []
        patch_root = patch_dirs[0] if patch_dirs else None

    if patch_root is not None:
        for label, filename in (
            ("changed dialogue cards", "dialogue_cards_changed.csv"),
            ("removed dialogue cards", "dialogue_cards_removed.csv"),
            ("added dialogue cards", "dialogue_cards_added.csv"),
            ("changed VO metadata", "vo_card_ids_changed.csv"),
            ("changed decoded VO audio", "vo_wav_audio_changed_common_card_ids.csv"),
        ):
            ids = load_card_ids_from_csv(patch_root / filename)
            if ids:
                invalid.update(ids)
                labels.append(f"{label} ({len(ids)})")
    return invalid, labels


def validate_pack_source(
    pack: str,
    source_root: Path,
    files: list[VoiceFile],
    official_vo_ids: set[str],
    invalid_silent_ids: set[str],
    invalid_silent_labels: list[str],
    official_vo_index: Path | None,
) -> None:
    errors: list[str] = []
    if not source_root.exists():
        raise SystemExit(f"Pack '{pack}' source folder does not exist: {source_root}")

    by_id: dict[str, list[str]] = {}
    audio_ids: set[str] = set()
    for file in files:
        by_id.setdefault(file.dialogue_id, []).append(file.relpath)
        if Path(file.relpath).suffix.lower() in VOICE_EXTENSIONS:
            audio_ids.add(file.dialogue_id)

    duplicate_groups = {card_id: paths for card_id, paths in by_id.items() if len(paths) > 1}
    if duplicate_groups:
        sample = "; ".join(f"{card_id}: {', '.join(paths[:4])}" for card_id, paths in list(sorted(duplicate_groups.items()))[:10])
        errors.append(f"duplicate dialogue IDs in pack '{pack}' ({len(duplicate_groups)} groups): {sample}")

    silent_ids, silent_errors = read_silent_card_ids(source_root)
    errors.extend(silent_errors)
    missing_silent_audio = sorted(silent_ids - audio_ids)
    if missing_silent_audio:
        errors.append(
            f"pack '{pack}' _silent-card-ids.txt lists IDs with no WAV/OGG ({len(missing_silent_audio)}): "
            + ", ".join(missing_silent_audio[:30])
        )

    if pack in SILENT_ONLY_PACKS:
        audio_not_in_silent = sorted(audio_ids - silent_ids - official_vo_ids)
        if audio_not_in_silent:
            errors.append(
                f"silent fallback pack '{pack}' has non-official audio not listed in _silent-card-ids.txt ({len(audio_not_in_silent)}): "
                + ", ".join(audio_not_in_silent[:30])
            )

    if invalid_silent_ids:
        silent_invalid_overlap = sorted(silent_ids & invalid_silent_ids)
        invalid_source = ", ".join(invalid_silent_labels) if invalid_silent_labels else str(official_vo_index)
        if silent_invalid_overlap:
            errors.append(
                f"pack '{pack}' silent fallback contains invalid/stale IDs from {invalid_source} "
                f"({len(silent_invalid_overlap)}): " + ", ".join(silent_invalid_overlap[:30])
            )

    if errors:
        detail = "\n  - ".join(errors)
        raise SystemExit(f"Refusing to build pack '{pack}' because validation failed:\n  - {detail}")


def file_bucket(file: VoiceFile, bucket_count: int) -> int:
    digest = hashlib.sha256(file.dialogue_id.lower().encode("utf-8")).hexdigest()
    return int(digest[:8], 16) % bucket_count


def split_shards(files: list[VoiceFile], max_shard_bytes: int, bucket_count: int) -> list[tuple[int, int, list[VoiceFile]]]:
    buckets: list[list[VoiceFile]] = [[] for _ in range(bucket_count)]
    for file in files:
        buckets[file_bucket(file, bucket_count)].append(file)

    shards: list[tuple[int, int, list[VoiceFile]]] = []
    for bucket, bucket_files in enumerate(buckets):
        current: list[VoiceFile] = []
        current_bytes = 0
        part = 0
        for file in sorted(bucket_files, key=lambda item: item.relpath.lower()):
            if current and current_bytes + file.size > max_shard_bytes:
                shards.append((bucket, part, current))
                current = []
                current_bytes = 0
                part += 1
            current.append(file)
            current_bytes += file.size
        if current:
            shards.append((bucket, part, current))
    return shards


def shard_fingerprint(files: Iterable[VoiceFile]) -> str:
    payload = [
        {"path": file.relpath, "bytes": file.size, "sha256": file.sha256}
        for file in files
    ]
    return sha256_text(stable_json(payload))


def write_zip(path: Path, files: list[VoiceFile]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    if tmp.exists():
        tmp.unlink()
    with zipfile.ZipFile(tmp, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for file in files:
            info = zipfile.ZipInfo(file.relpath, date_time=ZIP_DATE_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            with file.source.open("rb") as handle:
                archive.writestr(info, handle.read())
    if path.exists() and sha256_file(path) == sha256_file(tmp):
        tmp.unlink()
    else:
        tmp.replace(path)


def load_previous_manifest(output_root: Path, pack: str) -> dict[str, object] | None:
    path = output_root / "manifests" / f"{pack}.json"
    if not path.exists():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None


def reuse_existing_shard(zip_path: Path, old_shard: dict[str, object] | None, fingerprint: str) -> tuple[int, str] | None:
    if old_shard is None or not zip_path.exists():
        return None
    if str(old_shard.get("fingerprint", "")) != fingerprint:
        return None

    try:
        expected_size = int(old_shard.get("size", -1))
    except (TypeError, ValueError):
        expected_size = -1
    expected_sha = str(old_shard.get("sha256", ""))
    if expected_size < 0 or not expected_sha:
        return None

    actual_size = zip_path.stat().st_size
    if actual_size != expected_size:
        return None
    actual_sha = sha256_file(zip_path)
    if actual_sha != expected_sha:
        return None
    return actual_size, actual_sha


def build_shard(plan: ShardPlan) -> ShardBuild:
    reused = reuse_existing_shard(plan.zip_path, plan.old_shard, plan.fingerprint)
    if reused is None:
        write_zip(plan.zip_path, plan.files)
        zip_size = plan.zip_path.stat().st_size
        zip_sha = sha256_file(plan.zip_path)
        was_reused = False
        was_written = True
    else:
        zip_size, zip_sha = reused
        was_reused = True
        was_written = False

    return ShardBuild(
        index=plan.index,
        record={
            "name": plan.name,
            "path": plan.rel_shard_path,
            "url": f"{plan.base_url.rstrip('/')}/{plan.rel_shard_path}" if plan.base_url else "",
            "fingerprint": plan.fingerprint,
            "sha256": zip_sha,
            "size": zip_size,
            "uncompressedBytes": sum(file.size for file in plan.files),
            "fileCount": len(plan.files),
            "bucket": plan.bucket,
            "part": plan.part,
        },
        reused=was_reused,
        written=was_written,
    )


def build_pack(
    pack: str,
    display_name: str,
    source_root: Path,
    destination: str,
    output_root: Path,
    max_shard_bytes: int,
    bucket_count: int,
    base_url: str,
    clean: bool,
    prune_stale: bool,
    workers: int,
    update_message: str,
    official_vo_ids: set[str],
    invalid_silent_ids: set[str],
    invalid_silent_labels: list[str],
    official_vo_index: Path | None,
) -> BuildResult:
    files = collect_voice_files(source_root, workers=workers)
    validate_pack_source(pack, source_root, files, official_vo_ids, invalid_silent_ids, invalid_silent_labels, official_vo_index)
    shards = split_shards(files, max_shard_bytes, bucket_count)

    pack_dir = output_root / "packs" / pack / "shards"
    previous_manifest = None if clean else load_previous_manifest(output_root, pack)
    previous_shards = {
        str(shard.get("name", "")): shard
        for shard in (previous_manifest or {}).get("shards", [])
        if isinstance(shard, dict)
    }

    if clean and pack_dir.exists():
        shutil.rmtree(pack_dir)
    pack_dir.mkdir(parents=True, exist_ok=True)

    shard_records: list[dict[str, object]] = []
    file_records: dict[str, dict[str, object]] = {}
    current_shard_names: set[str] = set()
    shard_plans: list[ShardPlan] = []
    for index, (bucket, part, shard_files) in enumerate(shards):
        fingerprint = shard_fingerprint(shard_files)
        name = f"{pack}-b{bucket:03d}-p{part:02d}.zip"
        current_shard_names.add(name)
        rel_shard_path = f"packs/{pack}/shards/{name}"
        shard_plans.append(
            ShardPlan(
                index=index,
                bucket=bucket,
                part=part,
                files=shard_files,
                fingerprint=fingerprint,
                name=name,
                rel_shard_path=rel_shard_path,
                zip_path=output_root / rel_shard_path,
                old_shard=previous_shards.get(name),
                base_url=base_url,
            )
        )
        for file in shard_files:
            file_records[file.dialogue_id] = {
                "path": file.relpath,
                "shard": name,
                "sha256": file.sha256,
                "bytes": file.size,
            }

    if workers <= 1 or len(shard_plans) <= 1:
        shard_builds = [build_shard(plan) for plan in shard_plans]
    else:
        with ThreadPoolExecutor(max_workers=workers) as executor:
            shard_builds = list(executor.map(build_shard, shard_plans))
    shard_builds.sort(key=lambda item: item.index)

    reused_shards = sum(1 for item in shard_builds if item.reused)
    written_shards = sum(1 for item in shard_builds if item.written)
    shard_records = [item.record for item in shard_builds]

    manifest: dict[str, object] = {
        "schemaVersion": 2,
        "format": "sharded-zip",
        "pack": pack,
        "displayName": display_name,
        "destination": destination.replace("\\", "/"),
        "fileExtensions": sorted(VOICE_EXTENSIONS),
        "shardStrategy": "sha256-dialogue-id-bucket",
        "bucketCount": bucket_count,
        "maxShardBytes": max_shard_bytes,
        "fileCount": len(files),
        "sidecarFiles": sorted(VOICE_SIDECAR_FILES),
        "totalBytes": sum(file.size for file in files),
        "shardCount": len(shard_records),
        "shards": shard_records,
        "files": file_records,
    }
    hash_manifest = json.loads(stable_json(manifest))
    for shard in hash_manifest.get("shards", []):
        shard["url"] = ""
    manifest["manifestHash"] = sha256_text(stable_json(hash_manifest))
    manifest["version"] = str(manifest["manifestHash"])[:16]
    if update_message:
        manifest["updateMessage"] = update_message

    manifest_path = output_root / "manifests" / f"{pack}.json"
    changed_paths = [str(item.record["path"]) for item in shard_builds if item.written]
    manifest_text = json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n"
    if write_text_if_changed(manifest_path, manifest_text):
        changed_paths.append(f"manifests/{pack}.json")

    removed_stale = 0
    if prune_stale:
        for old_zip in sorted(pack_dir.glob("*.zip"), key=lambda item: item.name.lower()):
            if old_zip.name not in current_shard_names:
                old_zip.unlink()
                removed_stale += 1
                changed_paths.append(f"DELETE packs/{pack}/shards/{old_zip.name}")

    return BuildResult(
        manifest=manifest,
        reused_shards=reused_shards,
        written_shards=written_shards,
        removed_stale_shards=removed_stale,
        changed_paths=changed_paths,
    )


def parse_pack_spec(spec: str) -> tuple[str, str, str]:
    parts = spec.split("=", 1)
    if len(parts) != 2 or not parts[0].strip() or not parts[1].strip():
        raise argparse.ArgumentTypeError("pack spec must be name=relative/or/absolute/path")
    name = parts[0].strip()
    path = parts[1].strip()
    display = DEFAULT_PACKS.get(name, (name, ""))[0]
    return name, display, path


def compare_with_remote(output_root: Path, index: dict[str, object], remote_base_url: str) -> list[str]:
    changed: list[str] = []
    if not remote_base_url.strip():
        return changed

    remote_index = load_remote_json(join_url(remote_base_url, "manifest-index.json"))
    if remote_index is None or remote_index.get("indexHash") != index.get("indexHash"):
        changed.append("manifest-index.json")

    packs = index.get("packs", {})
    if not isinstance(packs, dict):
        return sorted(dict.fromkeys(changed), key=lambda item: item.lower())

    for pack_name in sorted(packs):
        local_manifest_path = output_root / "manifests" / f"{pack_name}.json"
        if not local_manifest_path.exists():
            continue
        try:
            local_manifest = json.loads(local_manifest_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue

        remote_manifest_path = f"manifests/{pack_name}.json"
        remote_manifest = load_remote_json(join_url(remote_base_url, remote_manifest_path))
        if remote_manifest is None:
            changed.append(remote_manifest_path)
            for shard in local_manifest.get("shards", []):
                if isinstance(shard, dict) and shard.get("path"):
                    changed.append(str(shard["path"]))
            continue

        if remote_manifest.get("manifestHash") != local_manifest.get("manifestHash"):
            changed.append(remote_manifest_path)

        remote_shards = {
            str(shard.get("name", "")): shard
            for shard in remote_manifest.get("shards", [])
            if isinstance(shard, dict) and shard.get("name")
        }
        local_shards = {
            str(shard.get("name", "")): shard
            for shard in local_manifest.get("shards", [])
            if isinstance(shard, dict) and shard.get("name")
        }

        for shard_name, local_shard in local_shards.items():
            remote_shard = remote_shards.get(shard_name)
            if remote_shard is None:
                changed.append(str(local_shard.get("path", "")))
                continue
            for key in ("sha256", "size", "fingerprint", "fileCount"):
                if remote_shard.get(key) != local_shard.get(key):
                    changed.append(str(local_shard.get("path", "")))
                    break

        for shard_name, remote_shard in remote_shards.items():
            if shard_name in local_shards:
                continue
            remote_path = str(remote_shard.get("path", ""))
            if remote_path:
                changed.append(f"DELETE {remote_path}")

    return sorted(dict.fromkeys([path for path in changed if path]), key=lambda item: item.lower().replace("delete ", ""))


def upload_changes_to_hf(
    output_root: Path,
    changed_paths: list[str],
    repo_id: str,
    repo_type: str,
    token: str,
    commit_message: str,
) -> None:
    if not changed_paths:
        print("HF upload: remote already matches local output.")
        return
    if not token:
        raise SystemExit("HF upload requested, but no token was provided. Set HF_TOKEN or HUGGINGFACE_HUB_TOKEN.")

    try:
        from huggingface_hub import CommitOperationAdd, CommitOperationDelete, HfApi
    except ImportError as exc:
        raise SystemExit("HF upload requested, but huggingface_hub is not installed.") from exc

    operations = []
    upload_count = 0
    delete_count = 0
    for item in changed_paths:
        if item.startswith("DELETE "):
            remote_path = item.removeprefix("DELETE ").strip().replace("\\", "/")
            if remote_path:
                operations.append(CommitOperationDelete(path_in_repo=remote_path))
                delete_count += 1
            continue

        relpath = item.strip().replace("\\", "/")
        if not relpath:
            continue
        source = output_root / relpath
        if not source.exists():
            raise SystemExit(f"HF upload wanted '{relpath}', but local file is missing: {source}")
        operations.append(CommitOperationAdd(path_in_repo=relpath, path_or_fileobj=str(source)))
        upload_count += 1

    if not operations:
        print("HF upload: no upload/delete operations were needed.")
        return

    print(f"HF upload: {upload_count} uploads, {delete_count} deletes -> {repo_type}:{repo_id}")
    api = HfApi(token=token)
    api.create_commit(
        repo_id=repo_id,
        repo_type=repo_type,
        operations=operations,
        commit_message=commit_message,
    )
    print("HF upload complete.")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--game-root", type=Path, default=Path.cwd())
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--base-url", default="")
    parser.add_argument("--shard-size-mb", type=int, default=512)
    parser.add_argument("--bucket-count", type=int, default=128)
    parser.add_argument("--workers", type=int, default=DEFAULT_WORKERS, help="Parallel workers for hashing, shard verification, and ZIP writing.")
    parser.add_argument("--pack", action="append", type=parse_pack_spec, default=[])
    parser.add_argument("--clean", action="store_true")
    parser.add_argument("--keep-stale", action="store_true", help="Keep unreferenced old shard zips instead of pruning them.")
    parser.add_argument(
        "--compare-remote-base-url",
        default=DEFAULT_REMOTE_COMPARE_BASE_URL,
        help=(
            "Optional remote root to compare against when writing publish-changes-remote.txt, "
            f"default: {DEFAULT_REMOTE_COMPARE_BASE_URL}. Pass an empty string to skip."
        ),
    )
    parser.add_argument("--upload", action="store_true", help="Upload changed pack files directly to the Hugging Face repo after building.")
    parser.add_argument(
        "--official-vo-index",
        type=Path,
        default=None,
        help=(
            "CSV of official VO streams used to prevent silent fallback packs from containing now-voiced cards. "
            "Defaults to the newest patch_compare_* VO index under --game-root, then voice_link_index/all_bnk_vo_dialogue/all_bnk_vo_streams.csv."
        ),
    )
    parser.add_argument("--skip-validation", action="store_true", help="Disable source pack validation. Only use for emergency manual recovery.")
    parser.add_argument("--hf-repo-id", default="zeroparade/dead_disco", help="Hugging Face repo id used by --upload.")
    parser.add_argument("--hf-repo-type", default="dataset", help="Hugging Face repo type used by --upload.")
    parser.add_argument(
        "--hf-token",
        default=os.environ.get("HF_TOKEN") or os.environ.get("HUGGINGFACE_HUB_TOKEN") or "",
        help="Hugging Face token for --upload. Prefer HF_TOKEN env var instead of passing this on the command line.",
    )
    parser.add_argument("--commit-message", default="Update ZERO PARADES voice packs", help="Commit message used by --upload.")
    parser.add_argument(
        "--message-dir",
        type=Path,
        default=None,
        help="Directory containing optional per-pack update message files named male.txt, female.txt, extras.txt, narrator.txt.",
    )
    parser.add_argument(
        "-c",
        "--changelog",
        action="append",
        type=parse_pack_message,
        default=[],
        help="Per-pack update message embedded in the manifest, for example -c \"male=Fixed 30 male lines\". May be repeated.",
    )
    args = parser.parse_args()

    game_root = args.game_root.resolve()
    output_root = args.output.resolve()
    output_root.mkdir(parents=True, exist_ok=True)
    message_dir = args.message_dir.resolve() if args.message_dir else output_root / "update-messages"
    cli_messages = {pack: message for pack, message in args.changelog}
    global_message = ""
    for key in ("pack", "all", "*"):
        if key in cli_messages:
            global_message = cli_messages.pop(key)
    if global_message:
        cli_messages = {name: global_message for name in DEFAULT_PACKS} | cli_messages
    official_vo_index = args.official_vo_index.resolve() if args.official_vo_index else find_default_official_vo_index(game_root)
    official_vo_ids = set() if args.skip_validation else load_official_vo_ids(official_vo_index)
    invalid_silent_ids, invalid_silent_labels = (set(), []) if args.skip_validation else load_invalid_silent_ids(game_root, official_vo_index, official_vo_ids)
    if args.skip_validation:
        print("WARNING: source validation is disabled.")
    elif official_vo_index is None:
        raise SystemExit("Could not find an official VO index for validation. Pass --official-vo-index or --skip-validation.")
    else:
        print(f"Using official VO validation index: {official_vo_index} ({len(official_vo_ids)} card IDs)")
        print(f"Invalid silent fallback set: {len(invalid_silent_ids)} IDs from {', '.join(invalid_silent_labels)}")

    pack_specs = args.pack
    if not pack_specs:
        pack_specs = [(name, display, rel) for name, (display, rel) in DEFAULT_PACKS.items()]

    index: dict[str, object] = {"schemaVersion": 1, "packs": {}}
    changed_paths: list[str] = []
    max_shard_bytes = max(1, args.shard_size_mb) * 1024 * 1024
    bucket_count = max(1, args.bucket_count)
    workers = clamp_workers(args.workers)
    print(f"Using {workers} workers")
    for name, display, source in pack_specs:
        source_path = Path(source)
        if not source_path.is_absolute():
            source_path = game_root / source_path
        destination = DEFAULT_PACKS.get(name, (display, source))[1] or source
        result = build_pack(
            pack=name,
            display_name=display,
            source_root=source_path,
            destination=destination,
            output_root=output_root,
            max_shard_bytes=max_shard_bytes,
            bucket_count=bucket_count,
            base_url=args.base_url,
            clean=args.clean,
            prune_stale=not args.keep_stale,
            workers=workers,
            update_message=cli_messages.get(name, read_update_message(message_dir / f"{name}.txt")),
            official_vo_ids=official_vo_ids,
            invalid_silent_ids=invalid_silent_ids,
            invalid_silent_labels=invalid_silent_labels,
            official_vo_index=official_vo_index,
        )
        manifest = result.manifest
        index["packs"][name] = {
            "manifest": f"manifests/{name}.json",
            "manifestHash": manifest["manifestHash"],
            "version": manifest["version"],
            "fileCount": manifest["fileCount"],
            "shardCount": manifest["shardCount"],
            "totalBytes": manifest["totalBytes"],
        }
        print(
            f"{name}: {manifest['fileCount']} files, {manifest['shardCount']} shards, "
            f"{result.reused_shards} reused, {result.written_shards} written, "
            f"{result.removed_stale_shards} stale removed, version {manifest['version']}"
        )
        changed_paths.extend(result.changed_paths)

    index["indexHash"] = sha256_text(stable_json(index))
    index_text = json.dumps(index, ensure_ascii=False, indent=2, sort_keys=True) + "\n"
    if write_text_if_changed(output_root / "manifest-index.json", index_text):
        changed_paths.append("manifest-index.json")
    changed_paths = sorted(dict.fromkeys(changed_paths), key=lambda item: item.lower())
    changed_list_text = "\n".join(changed_paths) + ("\n" if changed_paths else "No files changed.\n")
    write_text_if_changed(output_root / "publish-changes.txt", changed_list_text)
    remote_changed_paths: list[str] = []
    if args.compare_remote_base_url.strip() or args.upload:
        compare_base_url = args.compare_remote_base_url.strip() or DEFAULT_REMOTE_COMPARE_BASE_URL
        remote_changed_paths = compare_with_remote(output_root, index, compare_base_url)
        remote_changed_text = "\n".join(remote_changed_paths) + ("\n" if remote_changed_paths else "Remote already matches local output.\n")
        write_text_if_changed(output_root / "publish-changes-remote.txt", remote_changed_text)
        print(f"Remote publish paths: {len(remote_changed_paths)} ({output_root / 'publish-changes-remote.txt'})")
    print(f"Changed publish paths: {len(changed_paths)} ({output_root / 'publish-changes.txt'})")
    if args.upload:
        upload_changes_to_hf(
            output_root=output_root,
            changed_paths=remote_changed_paths,
            repo_id=args.hf_repo_id,
            repo_type=args.hf_repo_type,
            token=args.hf_token,
            commit_message=args.commit_message,
        )
        verify_changed_paths = compare_with_remote(output_root, index, compare_base_url)
        if verify_changed_paths:
            verify_text = "\n".join(verify_changed_paths) + "\n"
            write_text_if_changed(output_root / "publish-changes-remote-after-upload.txt", verify_text)
            raise SystemExit(
                f"HF upload finished, but remote still differs in {len(verify_changed_paths)} paths. "
                f"See {output_root / 'publish-changes-remote-after-upload.txt'}"
            )
        write_text_if_changed(output_root / "publish-changes-remote-after-upload.txt", "Remote matches local output.\n")
        print("HF upload verified: remote matches local output.")
    print(f"Wrote {output_root}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
