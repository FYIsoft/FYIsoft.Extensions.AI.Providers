"""Check exactly which files will be published; write a credential-free release manifest."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile


root = Path(__file__).resolve().parent.parent
directory = Path(sys.argv[1])
commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
manifest = []
expected_packages = set()
# Exact nuspec dependency minimums. Decision1 keeps these low so consumers on Azure.Identity 1.17.x
# (Azure.Core 1.50.0) and Microsoft.Extensions 10.0.9 restore without upgrades or NU1109/NU1605.
expected_dependencies = {
    "FYIsoft.Extensions.AI.Decision1": {
        "net10.0": {
            "Azure.Core": "1.50.0",
            "Microsoft.Extensions.Http": "10.0.9",
            "Microsoft.Extensions.Logging.Abstractions": "10.0.9",
            "Microsoft.Extensions.Options.ConfigurationExtensions": "10.0.9",
        },
    },
}
for package_id in ("FYIsoft.Extensions.AI.Anthropic", "FYIsoft.Extensions.AI.Decision1", "FYIsoft.Extensions.AI.Jev"):
    project = ET.parse(root / "src" / package_id / f"{package_id}.csproj")
    version = project.findtext(".//Version")
    package = directory / f"{package_id}.{version}.nupkg"
    expected_packages.add(package.name)
    required = {
        "_rels/.rels", "[Content_Types].xml", "README.md", f"{package_id}.nuspec",
        f"lib/net10.0/{package_id}.dll", f"lib/net10.0/{package_id}.xml",
    }
    with zipfile.ZipFile(package) as archive:
        names = set(archive.namelist())
        if len(names) != len(archive.namelist()) or not required.issubset(names):
            raise SystemExit(f"Missing or duplicate package entries: {package.name}")
        metadata = names - required
        if len(metadata) != 1 or not all(
            name.startswith("package/services/metadata/core-properties/") and name.endswith(".psmdcp")
            and ".." not in name and "\\" not in name for name in metadata
        ):
            raise SystemExit(f"Unexpected package payload: {package.name}")
        nuspec = ET.fromstring(archive.read(f"{package_id}.nuspec"))
        ns = {"n": "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"}
        repository = nuspec.find("n:metadata/n:repository", ns)
        if repository is None or repository.get("commit") != commit:
            raise SystemExit(f"Package source commit mismatch: {package.name}")
        if nuspec.findtext("n:metadata/n:id", namespaces=ns) != package_id or nuspec.findtext("n:metadata/n:version", namespaces=ns) != version:
            raise SystemExit(f"Package identity mismatch: {package.name}")
        if package_id in expected_dependencies:
            groups = {}
            for group in nuspec.findall("n:metadata/n:dependencies/n:group", ns):
                groups[group.get("targetFramework")] = {
                    dependency.get("id"): dependency.get("version")
                    for dependency in group.findall("n:dependency", ns)
                }
            if groups != expected_dependencies[package_id]:
                raise SystemExit(
                    f"Package dependency minimums changed: {package.name}\n"
                    f"  expected {expected_dependencies[package_id]}\n  actual   {groups}")
    manifest.append({"package": package.name, "version": version, "commit": commit,
                     "sha256": hashlib.sha256(package.read_bytes()).hexdigest(), "entries": sorted(names)})

if {p.name for p in directory.glob("*.nupkg")} != expected_packages:
    raise SystemExit("Unexpected packages in the publication directory.")
(directory / "release-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
print("Verified all SDK packages: only library DLLs, XML docs, README and NuGet metadata.")
