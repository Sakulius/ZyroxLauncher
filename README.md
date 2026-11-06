# Zyrox Launcher

Minecraft launcher for Windows with profiles, mods, modpacks, skins and several accounts. It installs the [Zyrox Client](https://github.com/Sakulius/ZyroxClient) on Fabric profiles.

Ready-made downloads are on the [Zyrox releases page](https://github.com/Sakulius/Zyrox/releases).

## Build

Needs the .NET 10 SDK.

```
dotnet build
```

To include the client, build [ZyroxClient](https://github.com/Sakulius/ZyroxClient) and copy its jar to `Assets/zyrox-client.jar`. Without it the launcher still runs, but the Zyrox Client option reports the jar as missing.

## Release

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist/app
ISCC.exe /DAppVersion=X.Y.Z Installer/zyrox.iss
```

## Sign-in

Your own build needs your own approved Azure app. Enter its ID as `AzureClientId` in `Services/AuthService.cs`.

## Credits

Built with [CmlLib.Core](https://github.com/CmlLib/CmlLib.Core), DiscordRichPresence and DnsClient. Inter font by The Inter Project Authors (SIL Open Font License, see `Assets/Fonts`).

## License

The code is under the [MIT License](LICENSE). It does not cover the Zyrox name and logo, so forks need their own.
