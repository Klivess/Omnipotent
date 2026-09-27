"""Render every observed telemetry route from the saved live baseline."""

import json
from datetime import datetime, timezone
from pathlib import Path


HERE = Path(__file__).resolve().parent


def read(name):
    return json.loads((HERE / name).read_text(encoding="utf-8-sig"))


last_day = {row["s"]: row for row in read("routes-24h.json")["routes"]}
last_week = read("routes-7d.json")["routes"]
collection = read("collection.json")
collected_at = datetime.fromisoformat(collection["collectedUtc"].replace("Z", "+00:00"))
long_view = read("routes-30d.json")
long_age_min = (collected_at - datetime.fromtimestamp(long_view["asOfUtc"] / 1000, timezone.utc)).total_seconds() / 60


def number(value):
    if value is None:
        return "—"
    return f"{value:,.0f}"


def duration(value):
    if value is None:
        return "—"
    if value < 1:
        return f"{value:.3f}"
    if value < 10:
        return f"{value:.2f}"
    if value < 100:
        return f"{value:.1f}"
    return number(value)


lines = [
    "# Observed API route inventory",
    "",
    f"Live telemetry collected {collected_at:%Y-%m-%d %H:%M} UTC, before the snapshot changes were deployed.",
    "All durations are server-side milliseconds. p95 is approximate from a log-scale histogram",
    "and is unstable for routes with few samples. A blank 24h entry means no observations",
    "in that window. Pseudo series such as `(preflight)` are telemetry categories.",
    "",
    "| Route series | 24h calls | 24h p95 ms | 7d calls | 7d p50 ms | 7d p95 ms | 7d max ms | 7d cumulative ms |",
    "|---|---:|---:|---:|---:|---:|---:|---:|",
]

for week in last_week:
    day = last_day.get(week["s"])
    values = [
        week["s"].replace("|", "\\|"),
        number(day["count"]) if day else "—",
        duration(day["p95"]) if day else "—",
        number(week["count"]),
        duration(week["p50"]),
        duration(week["p95"]),
        duration(week["max"]),
        number(week["costMs"]),
    ]
    lines.append("| " + " | ".join(values) + " |")

lines += [
    "",
    "Source: [24h](routes-24h.json), [7d](routes-7d.json),",
    f"[30d](routes-30d.json). The 30d preset was {long_age_min:.1f} minutes old at collection",
    "time. The table uses the seven-day view to represent recent traffic.",
    "",
]

(HERE / "route-inventory.md").write_text("\n".join(lines), encoding="utf-8")
