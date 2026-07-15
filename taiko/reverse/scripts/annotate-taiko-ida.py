"""Apply the recovered osu!taiko semantic names to the fingerprinted IDA database.

Run inside IDA with File -> Script file. This deliberately uses IDA's direct naming and
disassembly-comment APIs; it does not ask Hex-Rays to decompile synthetic managed-IL ranges.
"""

import ida_bytes
import ida_funcs
import ida_kernwin
import ida_name
import ida_nalt


SUPPORTED_SHA256 = "6e182c10d1813209d12753dbc70b3a5bba00fef4ecf64bc42051870e6dfe4b7d"

TARGETS = (
    (
        0xC24B0,
        "Taiko_Auto_GenerateReplayFrames",
        "Built-in Taiko Auto path: generates replay-frame state changes, not Player input.",
    ),
    (
        0xE1480,
        "Player_RecordInputStateChange",
        "Normal Player path: records a live packed input-state transition when state changes.",
    ),
    (
        0x6E900,
        "Input_PackFourButtonState",
        "Packs four logical button booleans into the pButtonState bit mask.",
    ),
    (
        0x128D10,
        "Input_InitializeDefaultBindings",
        "Initializes default bindings; Taiko inner X/C and outer Z/V are visible here.",
    ),
    (
        0xD1C10,
        "TaikoCircle_AcceptsNewPress",
        "Tests whether a new Taiko press is eligible for this circle and colour.",
    ),
    (
        0xD1DF0,
        "TaikoCircle_ResolveJudgement",
        "Resolves strict 300/100/miss timing and strong-note second-hand behavior.",
    ),
    (
        0x10B7D0,
        "Gameplay_DifficultyRange",
        "Piecewise linear OD interpolation used by recovered Taiko hit windows.",
    ),
    (
        0x1AD7A0,
        "TaikoDrumRoll_GetNativeTickInterval",
        "Computes native Taiko drumroll tick cadence and clamps it into 60..120 ms.",
    ),
    (
        0xB8930,
        "TaikoSpinner_InitializeRequiredHits",
        "Initializes Taiko spinner required-hit count, including OD and rate-mod scaling.",
    ),
)


def input_sha256():
    value = ida_nalt.retrieve_input_file_sha256()
    if value is None:
        return ""
    if isinstance(value, bytes):
        return value.hex()
    return bytes(value).hex()


def main():
    actual = input_sha256().lower()
    if actual != SUPPORTED_SHA256:
        raise RuntimeError(
            "Refusing to annotate a different target: expected %s, got %s"
            % (SUPPORTED_SHA256, actual or "<unavailable>")
        )

    applied = 0
    for address, name, comment in TARGETS:
        function = ida_funcs.get_func(address)
        if function is None or function.start_ea != address:
            raise RuntimeError("No function starts at 0x%X" % address)

        current = ida_name.get_name(address)
        if current != name and not ida_name.set_name(address, name, ida_name.SN_CHECK):
            raise RuntimeError(
                "Could not rename 0x%X from %r to %r" % (address, current, name)
            )
        if not ida_bytes.set_cmt(address, comment, False):
            existing = ida_bytes.get_cmt(address, False)
            if existing != comment:
                raise RuntimeError("Could not comment 0x%X" % address)

        ida_kernwin.msg("[taiko] 0x%X -> %s\n" % (address, name))
        applied += 1

    ida_kernwin.refresh_idaview_anyway()
    ida_kernwin.msg("[taiko] applied %d semantic annotations\n" % applied)


main()
