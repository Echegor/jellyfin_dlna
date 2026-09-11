"""Replay the captured tail read against actual historical stream implementations."""
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET

repo = Path(__file__).resolve().parents[1]
commits = ("20477c2", "0ceed2a", "6a63e19", "f815c88")
with tempfile.TemporaryDirectory(prefix="dlna-history-") as directory:
    output = Path(directory).resolve()
    for commit in commits:
        source = subprocess.check_output(
            ["git", "show", f"{commit}:src/Jellyfin.Plugin.Dlna.Playback/ProgressTrackingStream.cs"],
            cwd=repo, text=True,
        )
        source = "#nullable disable\n" + source.replace(
            "namespace Jellyfin.Plugin.Dlna.Playback;", f"namespace Historical.C{commit};"
        )
        (output / f"{commit}.cs").write_text(source)
    shutil.copy2(repo / "tests/historical/HistoricalTests.cs", output / "HistoricalTests.cs")
    project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    properties = ET.SubElement(project, "PropertyGroup")
    for name, value in {
        "TargetFramework": "net10.0", "IsTestProject": "true",
        "ImplicitUsings": "enable", "Nullable": "enable",
        "CopyLocalLockFileAssemblies": "true", "GenerateRuntimeConfigurationFiles": "true",
    }.items():
        ET.SubElement(properties, name).text = value
    items = ET.SubElement(project, "ItemGroup")
    ET.SubElement(items, "FrameworkReference", Include="Microsoft.AspNetCore.App")
    ET.SubElement(items, "ProjectReference", Include=str(
        repo / "tests/Jellyfin.Plugin.Dlna.Playback.Tests/Jellyfin.Plugin.Dlna.Playback.Tests.csproj"
    ))
    project_path = output / "Historical.csproj"
    ET.ElementTree(project).write(project_path, encoding="unicode")
    subprocess.run(["dotnet", "test", str(project_path), "--verbosity", "minimal"], check=True)
