# Third-party notices

## vroid-studio-mcp

`plugin/Reflection.cs` is adapted from
[brainrom/vroid-studio-mcp](https://github.com/brainrom/vroid-studio-mcp/tree/0696c3434677643d5e098405b42087542447213e),
whose [internal API notes](https://github.com/brainrom/vroid-studio-mcp/blob/0696c3434677643d5e098405b42087542447213e/docs/internal-api.md)
also informed the bridge. It is MIT-licensed; the notice is in
[`plugin/LICENSE.upstream`](plugin/LICENSE.upstream).

## Downloaded at build time

These are fetched by hash and never committed:

| Artifact | License |
| --- | --- |
| [VRoid Studio 2.16.0](https://vroid.com/en/studio) Windows installer | Proprietary (pixiv) |
| [BepInEx 6 IL2CPP be.788](https://github.com/BepInEx/BepInEx), including its bundled .NET runtime and Il2CppInterop | LGPL-2.1 for BepInEx; bundled components under their own licenses |
| [Unity 6000.0.62 base libraries](https://unity.bepinex.dev/libraries/) | Proprietary (Unity Technologies) |

The bridge plugin links against BepInEx, Il2CppInterop, and HarmonyX from that
bundle. VRoid is installed, and its app-specific interop assemblies are
generated from it, in local Nix builds; both stay in the user's Nix store.

## Framewisp

[Framewisp](https://github.com/krscott/framewisp) provides the private display.
The flake builds it with a one-line change from the Pixman renderer to GLES2.
