#!/usr/bin/env python3
"""List or extract unencrypted resources from a Godot 4 PCK archive."""

from __future__ import annotations

import argparse
import hashlib
import io
import re
import struct
from dataclasses import dataclass
from pathlib import Path, PurePosixPath

from PIL import Image


PCK_MAGIC = b"GDPC"
PACK_DIR_ENCRYPTED = 1
PACK_REL_FILEBASE = 2


@dataclass(frozen=True)
class PckEntry:
    path: str
    offset: int
    size: int
    digest: bytes
    flags: int


def read_exact(stream, size: int) -> bytes:
    data = stream.read(size)
    if len(data) != size:
        raise EOFError(f"Expected {size} bytes, received {len(data)}")
    return data


def read_entries(pck_path: Path) -> list[PckEntry]:
    with pck_path.open("rb") as stream:
        if read_exact(stream, 4) != PCK_MAGIC:
            raise ValueError(f"Not a Godot PCK archive: {pck_path}")

        pack_version, engine_major, engine_minor, engine_patch, pack_flags = struct.unpack(
            "<IIIII", read_exact(stream, 20)
        )
        file_base, directory_offset = struct.unpack("<QQ", read_exact(stream, 16))
        if pack_flags & PACK_DIR_ENCRYPTED:
            raise ValueError("Encrypted PCK directories are not supported")
        if pack_version != 3:
            raise ValueError(f"Unsupported PCK format version: {pack_version}")

        stream.seek(directory_offset)
        file_count = struct.unpack("<I", read_exact(stream, 4))[0]
        entries: list[PckEntry] = []
        for _ in range(file_count):
            path_size = struct.unpack("<I", read_exact(stream, 4))[0]
            raw_path = read_exact(stream, path_size)
            resource_path = raw_path.rstrip(b"\0").decode("utf-8")
            offset, size = struct.unpack("<QQ", read_exact(stream, 16))
            if pack_flags & PACK_REL_FILEBASE:
                offset += file_base
            digest = read_exact(stream, 16)
            flags = struct.unpack("<I", read_exact(stream, 4))[0]
            entries.append(PckEntry(resource_path, offset, size, digest, flags))

    print(
        f"PCK {pack_version}; Godot {engine_major}.{engine_minor}.{engine_patch}; "
        f"{len(entries)} resources"
    )
    return entries


def safe_relative_path(resource_path: str) -> Path:
    normalized = resource_path.removeprefix("res://").replace("\\", "/")
    pure_path = PurePosixPath(normalized)
    if pure_path.is_absolute() or ".." in pure_path.parts:
        raise ValueError(f"Unsafe resource path: {resource_path}")
    return Path(*pure_path.parts)


def extract_entry(pck_path: Path, entry: PckEntry, output_root: Path) -> None:
    target = output_root / safe_relative_path(entry.path)
    target.parent.mkdir(parents=True, exist_ok=True)
    with pck_path.open("rb") as stream:
        stream.seek(entry.offset)
        data = read_exact(stream, entry.size)
    if entry.digest != b"\0" * 16 and hashlib.md5(data).digest() != entry.digest:
        raise ValueError(f"MD5 mismatch while extracting {entry.path}")
    target.write_bytes(data)


def read_entry_data(pck_path: Path, entry: PckEntry) -> bytes:
    with pck_path.open("rb") as stream:
        stream.seek(entry.offset)
        data = read_exact(stream, entry.size)
    if entry.digest != b"\0" * 16 and hashlib.md5(data).digest() != entry.digest:
        raise ValueError(f"MD5 mismatch while reading {entry.path}")
    return data


def decode_compressed_texture(data: bytes, resource_path: str) -> Image.Image:
    signatures = (b"\x89PNG\r\n\x1a\n", b"RIFF", b"\xff\xd8\xff")
    offsets = [offset for signature in signatures if (offset := data.find(signature)) >= 0]
    if not offsets:
        raise ValueError(f"No embedded PNG, WebP, or JPEG image found in {resource_path}")
    image_offset = min(offsets)
    with Image.open(io.BytesIO(data[image_offset:])) as image:
        return image.convert("RGB").copy()


def export_card_portraits(pck_path: Path, entries: list[PckEntry], output_root: Path) -> None:
    entry_by_path = {entry.path.removeprefix("res://"): entry for entry in entries}
    import_prefix = "images/packed/card_portraits/"
    import_entries = [
        entry
        for entry in entries
        if entry.path.startswith(import_prefix) and entry.path.endswith(".png.import")
    ]
    exported = 0
    for import_entry in sorted(import_entries, key=lambda entry: entry.path):
        relative_import = PurePosixPath(import_entry.path.removeprefix(import_prefix))
        if "beta" in relative_import.parts or relative_import.name in {
            "beta.png.import",
            "ancient_beta.png.import",
        }:
            continue

        import_text = read_entry_data(pck_path, import_entry).rstrip(b"\0").decode("utf-8")
        match = re.search(r'^path="res://([^\"]+\.ctex)"$', import_text, flags=re.MULTILINE)
        if match is None:
            raise ValueError(f"Missing compressed texture path in {import_entry.path}")
        texture_path = match.group(1)
        texture_entry = entry_by_path.get(texture_path)
        if texture_entry is None:
            raise FileNotFoundError(f"PCK resource not found: {texture_path}")

        image = decode_compressed_texture(read_entry_data(pck_path, texture_entry), texture_path)
        target_relative = Path(*relative_import.parts).with_suffix("")
        target = output_root / "images" / "packed" / "card_portraits" / target_relative
        target.parent.mkdir(parents=True, exist_ok=True)
        image.save(target, format="PNG", optimize=True)
        exported += 1
        if exported % 50 == 0:
            print(f"Exported card portraits: {exported}", flush=True)

    print(f"Exported {exported} card portraits to {output_root}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("pck", type=Path)
    parser.add_argument("--contains", action="append", default=[])
    parser.add_argument("--output", type=Path)
    parser.add_argument(
        "--card-portraits",
        action="store_true",
        help="Decode non-beta card portraits into the source layout used by the model builders",
    )
    args = parser.parse_args()

    pck_path = args.pck.resolve()
    entries = read_entries(pck_path)
    if args.card_portraits:
        if args.output is None:
            parser.error("--card-portraits requires --output")
        export_card_portraits(pck_path, entries, args.output.resolve())
        return

    filters = [value.casefold() for value in args.contains]
    matches = [
        entry
        for entry in entries
        if not filters or all(value in entry.path.casefold() for value in filters)
    ]

    if args.output is None:
        for entry in matches:
            print(f"{entry.path}\t{entry.size}\tflags={entry.flags}")
        print(f"Matched {len(matches)} resources")
        return

    output_root = args.output.resolve()
    for index, entry in enumerate(matches, start=1):
        extract_entry(pck_path, entry, output_root)
        if index % 100 == 0 or index == len(matches):
            print(f"Extracted {index}/{len(matches)}", flush=True)


if __name__ == "__main__":
    main()
