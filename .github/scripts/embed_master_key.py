#!/usr/bin/env python3
"""Generate a build-only XOR-obfuscated master-key source file."""

import base64
import os
import secrets
import sys
from pathlib import Path


def read_dotenv(path: Path) -> str | None:
    if not path.is_file():
        return None

    for raw_line in path.read_text(encoding="utf-8-sig").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("export "):
            line = line[7:].lstrip()
        name, separator, value = line.partition("=")
        if separator and name.strip() == "TIDAL_OLD_AES_MASTER_KEY":
            value = value.strip()
            if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
                value = value[1:-1]
            return value
    return None


def csharp_bytes(values: bytes) -> str:
    return ", ".join(f"0x{value:02x}" for value in values)


def main() -> None:
    dotenv_path = Path(sys.argv[1])
    output_path = Path(sys.argv[2])
    key = os.environ.get("TIDAL_OLD_AES_MASTER_KEY") or read_dotenv(dotenv_path)
    if key:
        try:
            decoded = base64.b64decode(key, validate=True)
        except Exception as error:
            raise SystemExit("TIDAL_OLD_AES_MASTER_KEY must be valid base64.") from error
        if len(decoded) not in (16, 24, 32):
            raise SystemExit("TIDAL_OLD_AES_MASTER_KEY must decode to a 16, 24, or 32 byte AES key.")
        raw = key.encode("utf-8")
    else:
        raw = b""

    mask = secrets.token_bytes(len(raw))
    obfuscated = bytes(value ^ mask[index] for index, value in enumerate(raw))
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(
        "// Generated during the build; do not commit.\n"
        "#nullable enable\n"
        "namespace TidalMpvPlayer;\n"
        "internal static class BuildMasterKey\n{\n"
        f"    private static readonly byte[] Data = [{csharp_bytes(obfuscated)}];\n"
        f"    private static readonly byte[] Mask = [{csharp_bytes(mask)}];\n"
        "    internal static string? GetBase64()\n    {\n"
        "        if (Data.Length == 0) return null;\n"
        "        var value = new byte[Data.Length];\n"
        "        for (var i = 0; i < value.Length; i++) value[i] = (byte)(Data[i] ^ Mask[i]);\n"
        "        return System.Text.Encoding.UTF8.GetString(value);\n"
        "    }\n}\n",
        encoding="utf-8",
    )


if __name__ == "__main__":
    main()
