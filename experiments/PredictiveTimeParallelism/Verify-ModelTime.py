"""Independently replay the saved ModelTimeWindows inputs and accounting.

Uses Python's standard library only. This checks saved observations against the
ordered domain facts, deterministic replies, paired full traces and measured
call intervals. It does not prove influence-contract coverage or certificates.
"""

import argparse
import collections
import hashlib
import json
import math
from pathlib import Path
import sys


class VerificationError(Exception):
    pass


def require(condition, message):
    if not condition:
        raise VerificationError(message)


def verify(document):
    rows = document["Results"]
    require(bool(rows), "No measured results were saved.")
    groups = collections.defaultdict(list)
    decisions = 0
    intervals = 0
    strategies = {"serial", "coarse-safe", "timed-safe", "validated-speculation"}

    for row_index, row in enumerate(rows):
        label = f"row {row_index}: {row['Rule']}/{row['Wakes']}/{row['Strategy']}/{row['Seed']}"
        groups[(row["Rule"], row["Wakes"], row["Seed"])].append(row)
        values = [0] * 4
        generation = 0
        input_index = 0
        previous_tick = -1
        actor_rounds = []

        for fact in row["Facts"]:
            fields = fact.split(":")
            tick = int(fields[0])
            require(tick >= previous_tick, f"{label}: model time regressed.")
            previous_tick = tick
            if fields[1] == "topology":
                require(len(fields) == 3, f"{label}: malformed topology fact.")
                generation = int(fields[2])
            elif fields[1] == "delivery":
                require(len(fields) == 4, f"{label}: malformed delivery fact.")
                actor = int(fields[2])
                require(0 <= actor < len(values), f"{label}: invalid delivery actor.")
                values[actor] += int(fields[3])
            elif fields[1] == "decision":
                require(len(fields) == 5, f"{label}: malformed decision fact.")
                require(input_index < len(row["Inputs"]), f"{label}: missing actual input.")
                raw_input = row["Inputs"][input_index]
                actual = json.loads(raw_input)
                input_index += 1
                actor, round_number = int(fields[2]), int(fields[3])
                require(0 <= actor < 3, f"{label}: invalid deciding actor.")
                observed = (
                    actual["Actor"], actual["Round"], actual["ModelTime"],
                    actual["LocalValue"], actual["TopologyGeneration"],
                )
                expected = (actor, round_number, tick, values[actor], generation)
                require(observed == expected, f"{label}: input {input_index} differs from preceding facts.")
                require(actual["Version"] == 1 and actual["Protocol"] == "fixture-v1",
                        f"{label}: unsupported effective-input version/protocol.")
                require(actual["Rule"] == row["Rule"] and actual["FixtureSeed"] == row["Seed"],
                        f"{label}: effective rule/fixture identity differs.")
                reply = hashlib.sha256(raw_input.encode("utf-8")).digest()[0] % 3 + 1
                require(int(fields[4]) == reply, f"{label}: deterministic reply differs for input {input_index}.")
                actor_rounds.append((actor, round_number))
            else:
                raise VerificationError(f"{label}: unsupported fact kind {fields[1]!r}.")

        require(input_index == len(row["Inputs"]), f"{label}: unused actual inputs.")
        require(sorted(actor_rounds) == [(actor, round_number) for actor in range(3) for round_number in range(2)],
                f"{label}: the declared two-round, three-actor workload was not completed exactly once.")
        require(previous_tick == row["ModelTime"], f"{label}: final model time differs.")
        decisions += input_index

        calls = row["CallTimeline"]
        require(row["Calls"] == len(calls), f"{label}: call count differs from saved intervals.")
        require(sorted(call["Id"] for call in calls) == list(range(1, len(calls) + 1)),
                f"{label}: missing or duplicate call identities.")
        endpoints = []
        for call in calls:
            start, end = call["StartedMs"], call["CompletedMs"]
            require(math.isfinite(start) and math.isfinite(end) and 0 <= start < end,
                    f"{label}: invalid call interval {call['Id']}.")
            endpoints.extend(((start, 1), (end, -1)))
        active = peak = 0
        for _, delta in sorted(endpoints, key=lambda point: (point[0], point[1])):
            active += delta
            require(active >= 0, f"{label}: call interval accounting went negative.")
            peak = max(peak, active)
        require(active == 0 and peak == row["MaxInFlight"], f"{label}: peak/settled call count differs.")
        intervals += len(calls)

    for key, paired in groups.items():
        require(len(paired) == len(strategies) and {row["Strategy"] for row in paired} == strategies,
                f"{key}: missing or duplicate paired strategy.")
        reference = next(row for row in paired if row["Strategy"] == "serial")
        for row in paired:
            # Compare saved full values as well as reported hashes. Do not rely
            # on the report's hash-equality assertion or emulate .NET escaping.
            require(row["Inputs"] == reference["Inputs"] and row["Facts"] == reference["Facts"],
                    f"{key}/{row['Strategy']}: full paired trajectory differs.")
            require(row["InputHash"] == reference["InputHash"] and row["FactHash"] == reference["FactHash"],
                    f"{key}/{row['Strategy']}: reported paired trajectory hashes differ.")

    return {
        "Passed": True,
        "Rows": len(rows),
        "PairedConditions": len(groups),
        "PairedStrategies": sorted(strategies),
        "ReplayedDecisions": decisions,
        "ValidatedCallIntervals": intervals,
        "FullPairedInputsAndFactsCompared": True,
        "Scope": "saved model-time toy results; not influence-certificate coverage or Playground validation",
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path, help="Saved --model-time-check JSON output.")
    parser.add_argument("--output", type=Path, help="Write the independent verification JSON here.")
    args = parser.parse_args()
    source_hash = None
    try:
        raw = args.input.read_bytes()
        source_hash = hashlib.sha256(raw).hexdigest().upper()
        result = verify(json.loads(raw.decode("utf-8-sig")))
    except (OSError, ValueError, KeyError, TypeError, IndexError, VerificationError) as error:
        result = {"Passed": False, "ErrorType": type(error).__name__, "Error": str(error)}
    result["InputPath"] = str(args.input)
    result["InputSha256"] = source_hash
    result["VerifierSha256"] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest().upper()
    rendered = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
    if args.output is not None:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(rendered, encoding="utf-8", newline="\n")
    sys.stdout.write(rendered)
    return 0 if result["Passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
