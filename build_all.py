#!/usr/bin/env python3
"""Regenerate every committed gear set (run after changing gearset.py or the macro template).
Each entry is the exact gearset.py command line of that set."""
import subprocess, sys, os

HERE = os.path.dirname(os.path.abspath(__file__))
SETS = {
    "ratio-500_m1":                ["--ratio", "500"],
    "ratio-500_m1_sun16":          ["--ratio", "500", "--sun", "16"],
    "ratio-500_m1_planet32":       ["--ratio", "500", "--planet", "32", "--aw-max", "32", "--delta-max", "5",
                                    "--ecc-min", "8", "--max-wall", "20"],
    "ratio-528.7_m1_thin_ring":    ["--ratio", "1560695/2952", "--set", "16", "32", "115", "123", "158", "155"],
    "ratio-528.7_m1.25_thin_ring": ["--ratio", "1560695/2952", "--set", "16", "32", "115", "123", "158", "155", "--module", "1.25"],
    "ratio-528.7_m1.5_thin_ring":  ["--ratio", "1560695/2952", "--set", "16", "32", "115", "123", "158", "155", "--module", "1.5"],
    "ratio-528.7_m2_thin_ring":    ["--ratio", "1560695/2952", "--set", "16", "32", "115", "123", "158", "155", "--module", "2"],
}

if __name__ == "__main__":
    only = sys.argv[1:]
    for name, args in SETS.items():
        if only and name not in only:
            continue
        out = os.path.join(HERE, "output", name)
        cmd = [sys.executable, os.path.join(HERE, "gearset.py"), *args, "--out", out]
        print(">>", name)
        subprocess.run(cmd, check=True)
