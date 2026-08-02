{ pkgs ? import <nixpkgs> {} }:

pkgs.mkShell {
  packages = with pkgs; [
    dotnet-sdk_8
  ];

  LD_LIBRARY_PATH = pkgs.lib.makeLibraryPath (with pkgs; [
    fontconfig
    freetype
    libX11
    libICE
    libSM
    libXext
    libXi
    libXrandr
    libXcursor
    libXrender
    libxcb
    libglvnd
  ]);

  DOTNET_CLI_TELEMETRY_OPTOUT = "1";
  DOTNET_NOLOGO = "1";
}
