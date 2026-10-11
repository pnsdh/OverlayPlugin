# Packs the CEF bundle that CefInstaller downloads (out\CefSharp-<CEF version>-x64.7z) from the
# NuGet packages referenced by HtmlRenderer.csproj: CEF runtime, locales, CefSharp and its
# browser subprocess.  Release it on the fork's CEF repo under tag v<CEF version>, e.g.
#   gh release create v152.0.10 out\CefSharp-152.0.10-x64.7z -R pnsdh/OverlayPlugin-CEF
# and set CEF_VERSION in OverlayPlugin.Updater\CefInstaller.cs to the same version.
$ErrorActionPreference = "Stop"

if (Test-Path "C:\Program Files\7-Zip\7z.exe") {
    $ENV:PATH = "C:\Program Files\7-Zip;${ENV:PATH}";
}

$OutDir = Join-Path $PWD out

echo "==> Restoring HtmlRenderer to find the package folders..."

dotnet restore .\HtmlRenderer\HtmlRenderer.csproj
if (-not $?) { exit 1 }
dotnet msbuild .\HtmlRenderer\HtmlRenderer.csproj -t:PrintCEFDir -p:Configuration=Release
if (-not $?) { exit 1 }

function PackagePath($name) {
    return ([System.IO.File]::ReadAllText("$OutDir\Release\$name.Path.txt")).Trim()
}

$CefRedist = PackagePath "cef.redist.x64"
$CefSharpCommon = PackagePath "CefSharp.Common"
$CefSharpOffScreen = PackagePath "CefSharp.OffScreen"

$Sources = @(
    (Join-Path $CefRedist "runtimes\win-x64\native"),
    (Join-Path $CefRedist "CEF\win-x64"),
    (Join-Path $CefSharpCommon "lib\net462"),
    (Join-Path $CefSharpCommon "CefSharp\x64"),
    (Join-Path $CefSharpOffScreen "lib\net462")
)

$text = [System.IO.File]::ReadAllText("$CefRedist\README.txt");
$m = [regex]::Match($text, "CEF Version:\s*([0-9]+\.[0-9]+\.[0-9]+)");
if (-not $m.Success) {
    echo "Error: Version number not found in CEF's README.txt!"
    exit 1
}
$version = $m.Groups[1].Value

$stage = Join-Path $OutDir "cef-bundle"
if (Test-Path $stage) { rm -Recurse $stage }
mkdir $stage | Out-Null

foreach ($dir in $Sources) {
    echo "  $dir"
    cp -Recurse "$dir\*" $stage
}
Get-ChildItem $stage -Recurse -Include *.pdb, *.xml | Remove-Item
cp "$CefRedist\README.txt" (Join-Path $stage "README.txt")
cp "$CefRedist\LICENSE.txt" (Join-Path $stage "LICENSE.txt")
cp "$CefSharpCommon\LICENSE" (Join-Path $stage "CefSharp LICENSE.txt")

$archive = Join-Path $OutDir "CefSharp-$version-x64.7z"
if (Test-Path $archive) { rm $archive }

if (-not (Get-Command 7z -ErrorAction SilentlyContinue)) {
    echo "Error: 7z not found. The bundle is staged in $stage."
    exit 1
}

echo "==> Packing $archive..."
Push-Location $stage
7z a $archive .
$packed = $?
Pop-Location
if (-not $packed) { exit 1 }
