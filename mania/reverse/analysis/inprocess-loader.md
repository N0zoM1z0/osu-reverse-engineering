# CLR default-AppDomain loader and the historical replay-list proof

This page documents the bootstrap and the v0.2 replay-list experiment. The active v0.5.0 build uses
the same bootstrap but a normal Player-input executor; see [live-agent.md](live-agent.md).

## Runtime facts

The analysed `osu!.exe` is a PE32/x86 managed assembly with metadata runtime `v4.0.30319`. ILSpy
reconstructs it as a .NET Framework 4 project, and the host machine's CLR v4 understands the
standard AppDomain-manager activation environment variables:

```text
APPDOMAIN_MANAGER_ASM
APPDOMAIN_MANAGER_TYPE
```

The launcher sets those variables only in its own process environment, starts osu!, and immediately
restores its previous environment. The child inherits the values, so CLR loads the named
`AppDomainManager` into the default AppDomain before normal managed startup. No PE patch, native
remote thread, `DllMain` CLR host, or persistent machine-wide environment change is required.

The loader assembly must be available from the application base for the initial CLR bind. The
small loader then resolves the real plugin path from `MANIA_AUTO_PLUGIN`, loads it with
`Assembly.LoadFrom`, locates its entry point, and starts it inside `DefaultDomain`.

## Fingerprint lock

```text
File/Product version: 1.3.3.8
SHA-256: 6e182c10d1813209d12753dbc70b3a5bba00fef4ecf64bc42051870e6dfe4b7d
```

Obfuscated metadata names are unstable, so the plugin does not pretend its tokens form a public
API. It requires the exact file hash, resolves each token, and validates static/instance shape,
parameter count, declaring type relationships, and return/field type before use.

## Metadata targets recovered during the v0.2 proof

| Target | Token | Structural evidence |
|---|---:|---|
| selected mods field | `0x04000CC6` | static `osu_common.Mods` enum |
| current play mode method | `0x06002232` | static, zero arguments, returns `osu_common.PlayModes` |
| current beatmap getter | `0x06002C63` | static, zero arguments, returns beatmap object |
| beatmap path getter | `0x06001BF0` | instance, zero arguments, returns string |
| current score field | `0x040013C3` | static score reference |
| replay/source score field | `0x04002A7F` | static score reference |
| score replay-frame list | `0x04001980` | generic list of the recovered frame type |
| frame constructor | `0x0600219B` | `(int time, float x, float y, buttonState)` |
| frame x/y/time fields | `0x04001307`, `0x04001308`, `0x04001310` | mask, scroll speed, timestamp in mania |

## Historical replay-list experiment

The first proof deliberately used the built-in Autoplay path as an oracle:

1. Require the exact binary and structurally valid tokens.
2. Confirm current ruleset is mania.
3. Enable the Autoplay bit for the controlled test session.
4. Wait until the built-in generator has populated the typed replay list.
5. Parse the same `.osu` independently and build `(time, keyMask)` frames.
6. Compare count and every frame value with the built-in list.
7. Only on exact parity, construct a second typed list with the game's own frame constructor and
   replace references that still point to the old list.

Scroll speed was copied from the original initial frame and button state was `None`. Any mismatch
left the original list untouched. This proved that the default AppDomain, current beatmap, score
objects, and managed list could all be reached safely.

It was still not the intended final result. The game was consuming a pre-generated replay, not
observing an agent make decisions through the Player input route. `ReplayInjector.cs` is therefore
kept as source-level history but excluded from the active plugin build.

## Why the editor AiMod loader was not used

The stable editor contains a ruleset/AiMod assembly loader. It scans attributed DLLs and creates a
separate AppDomain for beatmap checking through a `MarshalByRefObject` boundary. That isolation is
appropriate for editor lint rules but gives the plugin a separate set of static game state. It is
not a convenient route to default-domain gameplay objects.

## Verification

The loader test host starts a fresh managed process with the same AppDomain-manager variables and
asserts that both loader and plugin run in `DefaultDomain`. The metadata probe uses reflection-only
loading to verify tokens and critical IL without starting gameplay. It also confirms the x86
Win32 `INPUT` structure is 28 bytes and that the active plugin assembly does not contain the
historical `ReplayInjector` type.
