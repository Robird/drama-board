#!/usr/bin/env python3
"""Analyze llm-runtime.jsonl call logs grouped by model.

Computes per-model latency / token / throughput statistics, plus a
breakdown by purpose (role-decision vs memory-maintenance) and cache
hit-rate analysis for the provider that reports token usage.
"""

import argparse
import json
import statistics
from collections import defaultdict
from pathlib import Path


def percentile(values, p):
    if not values:
        return None
    values = sorted(values)
    k = (len(values) - 1) * (p / 100.0)
    f = int(k)
    c = f + 1 if f + 1 < len(values) else f
    if c == f:
        return values[f]
    return values[f] + (values[c] - values[f]) * (k - f)


def fmt_ms(ms):
    if ms is None:
        return "  n/a"
    return f"{ms:8.1f}"


def rate(tokens, ms):
    """tokens (or chars) per second, based on service time."""
    if tokens is None or ms is None or ms <= 0:
        return None
    return tokens / (ms / 1000.0)


def aggregate(records):
    a = {"n": 0}
    for k in ["totalMs", "serviceMs", "completionTokens", "completionChars",
              "reasoningTokens", "promptTokens", "cacheReadTokens", "cacheMissTokens"]:
        a[k] = []
    for r in records:
        a["n"] += 1
        for k in ["totalMs", "serviceMs", "completionTokens", "completionChars",
                  "reasoningTokens", "promptTokens", "cacheReadTokens", "cacheMissTokens"]:
            v = r.get(k)
            if v is not None:
                a[k].append(v)
    return a


def summarize(a):
    def has(vals):
        return len(vals) > 0

    s = {"n": a["n"]}
    s["totalMs_avg"] = statistics.mean(a["totalMs"]) if has(a["totalMs"]) else None
    s["totalMs_med"] = statistics.median(a["totalMs"]) if has(a["totalMs"]) else None
    s["totalMs_p90"] = percentile(a["totalMs"], 90) if has(a["totalMs"]) else None
    s["totalMs_p95"] = percentile(a["totalMs"], 95) if has(a["totalMs"]) else None
    s["totalMs_sum"] = sum(a["totalMs"]) if has(a["totalMs"]) else None
    s["serviceMs_avg"] = statistics.mean(a["serviceMs"]) if has(a["serviceMs"]) else None
    s["completionTokens_sum"] = sum(a["completionTokens"]) if has(a["completionTokens"]) else None
    s["completionTokens_avg"] = statistics.mean(a["completionTokens"]) if has(a["completionTokens"]) else None
    s["completionTokens_med"] = statistics.median(a["completionTokens"]) if has(a["completionTokens"]) else None
    s["completionChars_avg"] = statistics.mean(a["completionChars"]) if has(a["completionChars"]) else None
    s["reasoningTokens_avg"] = statistics.mean(a["reasoningTokens"]) if has(a["reasoningTokens"]) else None
    # throughput
    svc = a["serviceMs"]
    s["tok_per_s"] = (sum(a["completionTokens"]) / (sum(svc) / 1000.0)) if (has(a["completionTokens"]) and svc and sum(svc) > 0) else None
    s["char_per_s"] = (sum(a["completionChars"]) / (sum(svc) / 1000.0)) if (has(a["completionChars"]) and svc and sum(svc) > 0) else None
    # cache (provider that reports tokens)
    if has(a["promptTokens"]) and has(a["cacheReadTokens"]):
        s["cache_hit_ratio"] = sum(a["cacheReadTokens"]) / sum(a["promptTokens"])
    else:
        s["cache_hit_ratio"] = None
    return s


def print_table(rows, headers):
    widths = [len(h) for h in headers]
    for row in rows:
        for i, cell in enumerate(row):
            widths[i] = max(widths[i], len(str(cell)))
    fmt = "  ".join("{:<%d}" % w for w in widths)
    print(fmt.format(*headers))
    print("  ".join("-" * w for w in widths))
    for row in rows:
        print(fmt.format(*[str(c) for c in row]))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("path", type=Path, help="path to llm-runtime.jsonl")
    args = parser.parse_args()

    records = []
    with open(args.path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            records.append(json.loads(line))

    print(f"总调用数: {len(records)}\n")

    by_model = defaultdict(list)
    by_model_purpose = defaultdict(lambda: defaultdict(list))
    for r in records:
        model = r["descriptor"]["model"]
        purpose = r["descriptor"]["purpose"]
        by_model[model].append(r)
        by_model_purpose[model][purpose].append(r)

    # ---- per-model overview ----
    print("=" * 78)
    print("一、按 model 汇总")
    print("=" * 78)
    headers = [
        "model", "n", "avg_ms", "med_ms", "p90_ms", "p95_ms",
        "sum_ms", "avg_tok", "med_tok", "sum_tok", "tok/s", "avg_char", "char/s",
    ]
    rows = []
    for model in by_model:
        s = summarize(aggregate(by_model[model]))
        rows.append([
            model,
            s["n"],
            f"{s['totalMs_avg']:.0f}" if s["totalMs_avg"] is not None else "n/a",
            f"{s['totalMs_med']:.0f}" if s["totalMs_med"] is not None else "n/a",
            f"{s['totalMs_p90']:.0f}" if s["totalMs_p90"] is not None else "n/a",
            f"{s['totalMs_p95']:.0f}" if s["totalMs_p95"] is not None else "n/a",
            f"{s['totalMs_sum']:.0f}" if s["totalMs_sum"] is not None else "n/a",
            f"{s['completionTokens_avg']:.1f}" if s["completionTokens_avg"] is not None else "n/a",
            f"{s['completionTokens_med']:.0f}" if s["completionTokens_med"] is not None else "n/a",
            f"{s['completionTokens_sum']:.0f}" if s["completionTokens_sum"] is not None else "n/a",
            f"{s['tok_per_s']:.1f}" if s["tok_per_s"] is not None else "n/a",
            f"{s['completionChars_avg']:.0f}" if s["completionChars_avg"] is not None else "n/a",
            f"{s['char_per_s']:.1f}" if s["char_per_s"] is not None else "n/a",
        ])
    print_table(rows, headers)

    # ---- per model + purpose ----
    print("\n" + "=" * 78)
    print("二、按 model x purpose 拆分")
    print("=" * 78)
    headers2 = [
        "model", "purpose", "n", "avg_ms", "med_ms", "p90_ms",
        "avg_tok", "tok/s", "avg_char", "char/s",
    ]
    rows2 = []
    for model in sorted(by_model_purpose):
        for purpose in sorted(by_model_purpose[model]):
            s = summarize(aggregate(by_model_purpose[model][purpose]))
            rows2.append([
                model,
                purpose,
                s["n"],
                f"{s['totalMs_avg']:.0f}" if s["totalMs_avg"] is not None else "n/a",
                f"{s['totalMs_med']:.0f}" if s["totalMs_med"] is not None else "n/a",
                f"{s['totalMs_p90']:.0f}" if s["totalMs_p90"] is not None else "n/a",
                f"{s['completionTokens_avg']:.1f}" if s["completionTokens_avg"] is not None else "n/a",
                f"{s['tok_per_s']:.1f}" if s["tok_per_s"] is not None else "n/a",
                f"{s['completionChars_avg']:.0f}" if s["completionChars_avg"] is not None else "n/a",
                f"{s['char_per_s']:.1f}" if s["char_per_s"] is not None else "n/a",
            ])
    print_table(rows2, headers2)

    # ---- deepseek cache / reasoning detail ----
    print("\n" + "=" * 78)
    print("三、token 使用明细 (仅 deepseek-v4-flash 上报 token 数据)")
    print("=" * 78)
    for model in by_model:
        s = summarize(aggregate(by_model[model]))
        if s["cache_hit_ratio"] is None:
            print(f"\n[{model}] 未上报 token 数据 (gpt-5.6-luna 场景) — 无法计算 token 级统计。")
            continue
        print(f"\n[{model}]")
        print(f"  avg promptTokens     : {statistics.mean(aggregate(by_model[model])['promptTokens']):.1f}")
        print(f"  avg reasoningTokens  : {s['reasoningTokens_avg']:.1f}  (占 completion 比例 "
              f"{s['reasoningTokens_avg']/s['completionTokens_avg']*100:.0f}%)")
        print(f"  avg cacheReadTokens  : {statistics.mean(aggregate(by_model[model])['cacheReadTokens']):.1f}")
        print(f"  avg cacheMissTokens  : {statistics.mean(aggregate(by_model[model])['cacheMissTokens']):.1f}")
        print(f"  cache hit ratio      : {s['cache_hit_ratio']*100:.1f}%")


if __name__ == "__main__":
    main()
