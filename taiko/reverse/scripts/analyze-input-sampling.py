#!/usr/bin/env python3
"""Compare a native Taiko plan with the key edges recorded in a local .osr.

The replay's player-name field is consumed and discarded. Nothing is uploaded and
no account value is printed. The per-hand alignment is most exact for CLEAN runs;
humanized runs remain useful when --edge-window covers their configured offsets.
"""

from __future__ import annotations

import argparse
import bisect
import lzma
import math
import statistics
import struct
from dataclasses import dataclass
from pathlib import Path
from typing import BinaryIO, Iterable


EASY = 0x2
HARD_ROCK = 0x10
DOUBLE_TIME = 0x40
HALF_TIME = 0x100
KEY_BITS = (1, 4, 2, 8)  # InnerLeft, InnerRight, OuterLeft, OuterRight.


@dataclass(frozen=True)
class Replay:
    mode: int
    version: int
    beatmap_hash: str
    counts: tuple[int, int, int, int, int, int]
    mods: int
    frames: tuple[tuple[int, int], ...]
    downs: tuple[tuple[int, ...], ...]


@dataclass(frozen=True)
class TimingPoint:
    time: float
    beat_length: float
    uninherited: bool
    line: int


@dataclass(frozen=True)
class HitObject:
    start: int
    end: int
    kind: str
    sound: int
    beat_length: float
    velocity: float
    line: int


@dataclass(frozen=True)
class PlannedDown:
    time: int
    key: int
    required: bool
    object_id: int | None
    strong: bool
    line: int
    kind: str


def read_uleb128(stream: BinaryIO) -> int:
    value = 0
    shift = 0
    while True:
        raw = stream.read(1)
        if not raw:
            raise EOFError("truncated ULEB128")
        byte = raw[0]
        value |= (byte & 0x7F) << shift
        if not byte & 0x80:
            return value
        shift += 7


def read_osu_string(stream: BinaryIO) -> str:
    tag = stream.read(1)
    if tag == b"\x00":
        return ""
    if tag != b"\x0b":
        raise ValueError(f"invalid osu! string tag {tag!r}")
    return stream.read(read_uleb128(stream)).decode("utf-8", "replace")


def read_replay(path: Path) -> Replay:
    with path.open("rb") as stream:
        mode = struct.unpack("<B", stream.read(1))[0]
        version = struct.unpack("<i", stream.read(4))[0]
        beatmap_hash = read_osu_string(stream)
        read_osu_string(stream)  # Player name: deliberately discarded.
        read_osu_string(stream)  # Replay hash.
        counts = struct.unpack("<6h", stream.read(12))
        stream.read(4 + 2 + 1)
        mods = struct.unpack("<i", stream.read(4))[0]
        read_osu_string(stream)  # Life graph.
        stream.read(8)
        compressed_length = struct.unpack("<i", stream.read(4))[0]
        compressed = stream.read(compressed_length)

    payload = lzma.decompress(compressed, format=lzma.FORMAT_ALONE).decode(
        "utf-8", "replace"
    )
    time = 0
    previous = 0
    frames: list[tuple[int, int]] = []
    downs: list[list[int]] = [[] for _ in KEY_BITS]
    for encoded in payload.split(","):
        fields = encoded.split("|")
        if len(fields) < 4:
            continue
        delta = int(fields[0])
        state = int(float(fields[3]))
        if delta == -12345:
            continue
        time += delta
        frames.append((time, state))
        added = state & ~previous
        for key, bit in enumerate(KEY_BITS):
            if added & bit:
                downs[key].append(time)
        previous = state
    return Replay(
        mode,
        version,
        beatmap_hash,
        counts,
        mods,
        tuple(frames),
        tuple(tuple(values) for values in downs),
    )


def difficulty_range(value: float, minimum: float, middle: float, maximum: float) -> float:
    if value > 5.0:
        return middle + (maximum - middle) * (value - 5.0) / 5.0
    return middle - (middle - minimum) * (5.0 - value) / 5.0


def modified_od(overall_difficulty: float, mods: int) -> float:
    value = overall_difficulty
    if mods & EASY:
        value = max(0.0, value / 2.0)
    if mods & HARD_ROCK:
        value = min(10.0, value * 1.4)
    return value


def clock_rate(mods: int) -> float:
    if mods & DOUBLE_TIME:
        return 1.5
    if mods & HALF_TIME:
        return 0.75
    return 1.0


def parse_beatmap(path: Path) -> tuple[int, float, float, list[HitObject]]:
    section = ""
    format_version = -1
    overall_difficulty = math.nan
    slider_multiplier = math.nan
    slider_tick_rate = 1.0
    timing_points: list[TimingPoint] = []
    raw_objects: list[tuple[int, str]] = []

    with path.open("r", encoding="utf-8-sig") as stream:
        for line_number, raw in enumerate(stream, 1):
            line = raw.strip()
            if line_number == 1 and line.lower().startswith("osu file format v"):
                format_version = int(line[17:])
                continue
            if not line or line.startswith("//"):
                continue
            if line.startswith("[") and line.endswith("]"):
                section = line[1:-1].strip()
                continue
            if section == "TimingPoints":
                fields = line.split(",")
                beat_length = float(fields[1])
                timing_points.append(
                    TimingPoint(
                        float(fields[0]),
                        beat_length,
                        len(fields) < 7 or int(fields[6]) != 0,
                        line_number,
                    )
                )
                continue
            if section == "HitObjects":
                raw_objects.append((line_number, line))
                continue
            if ":" not in line:
                continue
            key, value = (part.strip() for part in line.split(":", 1))
            if section == "General" and key == "Mode" and int(value) != 1:
                raise ValueError(f"{path}: expected native Mode:1")
            if section == "Difficulty":
                if key == "OverallDifficulty":
                    overall_difficulty = float(value)
                elif key == "SliderMultiplier":
                    slider_multiplier = float(value)
                elif key == "SliderTickRate":
                    slider_tick_rate = float(value)

    timing_points.sort(key=lambda point: (point.time, point.line))

    def timing_at(time: int) -> tuple[float, float]:
        beat_length = math.nan
        velocity = 1.0
        for point in timing_points:
            if point.time > time:
                break
            if point.uninherited:
                beat_length = point.beat_length
                velocity = 1.0
            else:
                velocity = max(0.1, min(10.0, -100.0 / point.beat_length))
        if math.isnan(beat_length):
            beat_length = next(
                point.beat_length
                for point in timing_points
                if point.uninherited and point.beat_length > 0.0
            )
        return beat_length, velocity

    objects: list[HitObject] = []
    for line_number, text in raw_objects:
        fields = text.split(",")
        start = int(fields[2])
        object_type = int(fields[3])
        sound = int(fields[4])
        if object_type & 1:
            objects.append(HitObject(start, start, "circle", sound, 0.0, 1.0, line_number))
        elif object_type & 2:
            repeat = int(fields[6])
            pixel_length = float(fields[7])
            beat_length, velocity = timing_at(start)
            duration = (
                pixel_length
                * 1.4
                * repeat
                * beat_length
                / (slider_multiplier * 100.0 * velocity)
            )
            objects.append(
                HitObject(
                    start,
                    start + int(duration),
                    "roll",
                    sound,
                    beat_length,
                    velocity,
                    line_number,
                )
            )
        elif object_type & 8:
            objects.append(
                HitObject(start, int(fields[5]), "spinner", sound, 0.0, 1.0, line_number)
            )
        else:
            raise ValueError(f"{path}:{line_number}: unsupported object type {object_type}")
    objects.sort(key=lambda hit_object: (hit_object.start, hit_object.line))
    return format_version, overall_difficulty, slider_tick_rate, objects


def build_clean_plan(path: Path, mods: int) -> tuple[list[PlannedDown], int]:
    format_version, overall_difficulty, slider_tick_rate, objects = parse_beatmap(path)
    difficulty = modified_od(overall_difficulty, mods)
    strikes: list[PlannedDown] = []
    prefer_left = True
    object_id = 0

    for hit_object in objects:
        if hit_object.kind == "circle":
            kat = bool(hit_object.sound & (2 | 8))
            strong = bool(hit_object.sound & 4)
            left, right = ((2, 3) if kat else (0, 1))
            keys = (left, right) if strong else ((left if prefer_left else right),)
            strikes.extend(
                PlannedDown(
                    hit_object.start,
                    key,
                    True,
                    object_id,
                    strong,
                    hit_object.line,
                    "circle",
                )
                for key in keys
            )
            object_id += 1
        elif hit_object.kind == "roll":
            if format_version < 8:
                interval = hit_object.beat_length / hit_object.velocity / 8.0
            else:
                special = slider_tick_rate in (1.5, 3.0, 6.0)
                interval = hit_object.beat_length / (6.0 if special else 8.0)
            while interval < 60.0:
                interval *= 2.0
            while interval > 120.0:
                interval /= 2.0
            exact = float(hit_object.start)
            previous = -(2**31)
            while exact < hit_object.end:
                time = int(exact)
                if time != previous:
                    strikes.append(
                        PlannedDown(
                            time,
                            0 if prefer_left else 1,
                            False,
                            None,
                            False,
                            hit_object.line,
                            "roll",
                        )
                    )
                    prefer_left = not prefer_left
                    previous = time
                exact += interval
        else:
            duration = hit_object.end - hit_object.start
            rate = difficulty_range(difficulty, 3.0, 5.0, 7.5)
            base_required = int((duration / 1000.0) * rate)
            required = max(1, int(base_required * 1.65))
            if mods & DOUBLE_TIME:
                required = max(1, int(required * 0.75))
            if mods & HALF_TIME:
                required = max(1, int(required * 1.5))
            count = required + 1
            interval = max(1, duration // count)
            cycle = (0, 2, 1, 3)
            for index in range(count):
                time = hit_object.start + index * interval
                if time >= hit_object.end:
                    break
                strikes.append(
                    PlannedDown(
                        time,
                        cycle[index % 4],
                        False,
                        None,
                        False,
                        hit_object.line,
                        "spinner",
                    )
                )
        prefer_left = not prefer_left

    circle_edges: dict[int, list[PlannedDown]] = {}
    for strike in strikes:
        if strike.required:
            assert strike.object_id is not None
            circle_edges.setdefault(strike.object_id, []).append(strike)
    circles = sorted(circle_edges.values(), key=lambda edges: edges[0].time)
    acceptance = int(difficulty_range(difficulty, 200.0, 150.0, 100.0))
    protected: list[PlannedDown] = []
    for strike in strikes:
        if strike.kind != "roll":
            protected.append(strike)
            continue
        unsafe = False
        for circle in circles:
            circle_time = circle[0].time
            if circle_time < strike.time - acceptance:
                continue
            if circle_time > strike.time + acceptance:
                break
            if strike.time > circle_time:
                continue
            if strike.time == circle_time and any(
                strike.key == edge.key for edge in circle
            ):
                continue
            unsafe = True
            break
        if not unsafe:
            protected.append(strike)
    strikes = protected

    coalesced: dict[tuple[int, int], PlannedDown] = {}
    for strike in strikes:
        identity = (strike.time, strike.key)
        previous = coalesced.get(identity)
        if previous is None or strike.required and not previous.required:
            coalesced[identity] = strike
    return sorted(coalesced.values(), key=lambda strike: (strike.time, strike.key)), object_id


def align_edges(
    expected: Iterable[PlannedDown], actual: tuple[int, ...], window: int
) -> list[tuple[PlannedDown, int | None]]:
    result: list[tuple[PlannedDown, int | None]] = []
    cursor = 0
    for strike in sorted(expected, key=lambda item: item.time):
        while cursor < len(actual) and actual[cursor] < strike.time - window:
            cursor += 1
        candidates = [
            index
            for index in range(cursor, min(cursor + 4, len(actual)))
            if actual[index] <= strike.time + window
        ]
        if not candidates:
            result.append((strike, None))
            continue
        best = min(candidates, key=lambda index: abs(actual[index] - strike.time))
        result.append((strike, actual[best]))
        cursor = best + 1
    return result


def percentile(values: list[int], fraction: float) -> int:
    ordered = sorted(values)
    return ordered[int((len(ordered) - 1) * fraction)]


def frame_model_misses(
    plan: list[PlannedDown], object_count: int, frame_times: list[int], pulse: int
) -> int:
    by_key = [[strike for strike in plan if strike.key == key] for key in range(4)]

    def has_frame(start: int, end: int) -> bool:
        index = bisect.bisect_left(frame_times, start)
        return index < len(frame_times) and frame_times[index] <= end

    objects: dict[int, list[bool]] = {index: [] for index in range(object_count)}
    for strikes in by_key:
        previous_up = -(2**31)
        for index, strike in enumerate(strikes):
            next_down = strikes[index + 1].time if index + 1 < len(strikes) else None
            release = strike.time + pulse
            if next_down is not None and next_down <= release:
                release = next_down - 1
            release = max(strike.time + 1, release)
            down_seen = has_frame(strike.time, release)
            reset_seen = index == 0 or has_frame(previous_up + 1, strike.time - 1)
            if strike.required and strike.object_id is not None:
                objects[strike.object_id].append(down_seen and reset_seen)
            previous_up = release
    return sum(not any(states) for states in objects.values())


def describe_distribution(label: str, values: list[int]) -> None:
    if not values:
        print(f"{label}: none")
        return
    print(
        f"{label}: min={min(values)} p10={percentile(values, 0.10)} "
        f"p50={statistics.median(values):g} p90={percentile(values, 0.90)} "
        f"p99={percentile(values, 0.99)} max={max(values)}"
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("replay", type=Path)
    parser.add_argument("beatmap", type=Path)
    parser.add_argument("--edge-window", type=int, default=55)
    parser.add_argument("--old-map-pulse", type=int, default=8)
    parser.add_argument("--physical-pulse", type=int, default=30)
    args = parser.parse_args()
    if args.edge_window < 1 or args.old_map_pulse < 1 or args.physical_pulse < 1:
        parser.error("windows and pulses must be positive")

    replay = read_replay(args.replay)
    if replay.mode != 1:
        raise ValueError(f"expected a Taiko replay, got mode {replay.mode}")
    plan, object_count = build_clean_plan(args.beatmap, replay.mods)
    aligned: list[tuple[PlannedDown, int | None]] = []
    for key in range(4):
        aligned.extend(
            align_edges(
                (strike for strike in plan if strike.key == key),
                replay.downs[key],
                args.edge_window,
            )
        )

    required = [(strike, actual) for strike, actual in aligned if strike.required]
    object_edges: dict[int, list[int | None]] = {index: [] for index in range(object_count)}
    for strike, actual in required:
        assert strike.object_id is not None
        object_edges[strike.object_id].append(actual)
    missing_edges = sum(actual is None for _, actual in required)
    missing_objects = sum(all(actual is None for actual in values) for values in object_edges.values())
    partial_objects = sum(
        any(actual is None for actual in values)
        and not all(actual is None for actual in values)
        for values in object_edges.values()
    )

    frame_times = [time for time, _ in replay.frames]
    frame_gaps = [
        right - left
        for left, right in zip(frame_times, frame_times[1:])
        if left >= 0 and right > left
    ]
    offsets = [actual - strike.time for strike, actual in required if actual is not None]
    next_frame_matched: list[int] = []
    next_frame_missing: list[int] = []
    for strike, actual in required:
        index = bisect.bisect_left(frame_times, strike.time)
        if index >= len(frame_times):
            continue
        target = next_frame_missing if actual is None else next_frame_matched
        target.append(frame_times[index] - strike.time)

    rate = clock_rate(replay.mods)
    new_map_pulse = math.ceil(args.physical_pulse * rate)
    print(f"replay={args.replay.name}")
    print(f"beatmap={args.beatmap.name}")
    print(
        f"mode={replay.mode} version={replay.version} beatmap-md5={replay.beatmap_hash} "
        f"mods=0x{replay.mods:X} clock-rate={rate:.2f}x"
    )
    print(
        "score-counts="
        f"300:{replay.counts[0]}/100:{replay.counts[1]}/50:{replay.counts[2]}/"
        f"geki:{replay.counts[3]}/katu:{replay.counts[4]}/miss:{replay.counts[5]}"
    )
    print(
        f"frames={len(replay.frames)} recorded-key-downs={sum(map(len, replay.downs))} "
        f"planned-key-downs={len(plan)} circles={object_count}"
    )
    describe_distribution("frame-gap-map-ms", frame_gaps)
    describe_distribution("matched-edge-offset-map-ms", offsets)
    describe_distribution("next-frame-delay-matched-map-ms", next_frame_matched)
    describe_distribution("next-frame-delay-missing-map-ms", next_frame_missing)
    print(
        f"required-edges-missing={missing_edges} objects-all-edges-missing={missing_objects} "
        f"strong-objects-partial={partial_objects} edge-window=+/-{args.edge_window}ms"
    )
    print(
        f"frame-grid-exposure old={args.old_map_pulse}map-ms:"
        f"{frame_model_misses(plan, object_count, frame_times, args.old_map_pulse)} "
        f"new={args.physical_pulse}real-ms/{new_map_pulse}map-ms:"
        f"{frame_model_misses(plan, object_count, frame_times, new_map_pulse)}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
