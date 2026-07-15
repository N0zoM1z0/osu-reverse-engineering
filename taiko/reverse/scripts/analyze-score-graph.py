#!/usr/bin/env python3
"""Decode judgement timestamps from an osu!stable .osg score-graph file.

The supported target writes an eight-byte header followed by fixed 29-byte
snapshots. The format contains score state, not a player name. This tool reads
one explicitly supplied local file and performs no network access.
"""

from __future__ import annotations

import argparse
import struct
from dataclasses import dataclass
from pathlib import Path


HEADER = struct.Struct("<ii")
RECORD = struct.Struct("<iB6HiHH4s")
COUNT_NAMES = ("300", "100", "50", "geki", "katu", "miss")
NON_300_INDEXES = (1, 2, 4, 5)


@dataclass(frozen=True)
class ScoreGraphRecord:
    time: int
    counts: tuple[int, int, int, int, int, int]
    score: int
    combo: int
    maximum_combo: int


def read_score_graph(path: Path) -> tuple[int, tuple[ScoreGraphRecord, ...]]:
    data = path.read_bytes()
    if len(data) < HEADER.size:
        raise ValueError(f"{path}: truncated score-graph header")
    version, count = HEADER.unpack_from(data)
    if count < 0:
        raise ValueError(f"{path}: negative record count {count}")
    expected = HEADER.size + count * RECORD.size
    if len(data) != expected:
        raise ValueError(
            f"{path}: expected {expected} bytes for {count} records, got {len(data)}"
        )

    records: list[ScoreGraphRecord] = []
    previous_counts = (0, 0, 0, 0, 0, 0)
    for index in range(count):
        offset = HEADER.size + index * RECORD.size
        unpacked = RECORD.unpack_from(data, offset)
        counts = tuple(unpacked[2:8])
        if any(current < previous for current, previous in zip(counts, previous_counts)):
            raise ValueError(f"{path}: judgement counts move backwards at record {index}")
        records.append(
            ScoreGraphRecord(
                time=unpacked[0],
                counts=counts,
                score=unpacked[8],
                combo=unpacked[10],
                maximum_combo=unpacked[9],
            )
        )
        previous_counts = counts
    return version, tuple(records)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("score_graph", type=Path, help="local .osg file from osu!/Data/r")
    parser.add_argument(
        "--all-judgements",
        action="store_true",
        help="also print every 300/geki increment (normally omitted for readability)",
    )
    args = parser.parse_args()

    version, records = read_score_graph(args.score_graph)
    print(
        f"score-graph={args.score_graph.name} version={version} "
        f"records={len(records)} record-size={RECORD.size}"
    )
    if not records:
        print("final-counts=300:0/100:0/50:0/geki:0/katu:0/miss:0")
        return 0

    final = records[-1]
    print(
        "final-counts="
        + "/".join(f"{name}:{value}" for name, value in zip(COUNT_NAMES, final.counts))
        + f" score={final.score} max-combo={final.maximum_combo}"
    )

    selected = range(6) if args.all_judgements else NON_300_INDEXES
    previous = (0, 0, 0, 0, 0, 0)
    event_count = 0
    for record in records:
        delta = tuple(current - old for current, old in zip(record.counts, previous))
        for count_index in selected:
            for _ in range(delta[count_index]):
                print(
                    f"time={record.time}ms judgement={COUNT_NAMES[count_index]} "
                    f"combo={record.combo} score={record.score}"
                )
                event_count += 1
        previous = record.counts
    print(f"printed-judgements={event_count}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
