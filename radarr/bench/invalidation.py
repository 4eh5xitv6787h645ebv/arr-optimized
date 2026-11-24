#!/usr/bin/env python3
"""Live invalidation check on a running clone (no restart between steps).

  invalidation.py NAME MOVIE_ID

1. adds a custom format matching every title; a search must show it on every release that
   reaches custom-format evaluation (mapped to the movie)
2. raises its score in the movie's quality profile; the next search must add exactly that score
3. edits the format to match nothing; the next search must drop it everywhere
4. deletes it; the next search must not show it
Prints a JSON summary. Everything is undone at the end.
"""
import json
import subprocess
import sys
import time

import lab


def api(name, method, path, body=None):
    r = subprocess.run(["docker", "exec", "-i", "radarr-opt-client", "python", "-I", "/bench/api.py",
                        f"radarr-opt-{name}", lab.api_key(name), method, path],
                       input=json.dumps(body).encode() if body is not None else b"", capture_output=True, check=True)
    return json.loads(r.stdout) if r.stdout else None


def search(name, movie):
    time.sleep(2.5)
    return api(name, "GET", f"/api/v3/release?movieId={movie}")


def main():
    name, movie = sys.argv[1], sys.argv[2]
    summary = {}
    cf = api(name, "POST", "/api/v3/customformat", {
        "name": "radarr-next invalidation probe", "includeCustomFormatWhenRenaming": False,
        "specifications": [{"name": "all", "implementation": "ReleaseTitleSpecification", "negate": False,
                            "required": True, "fields": [{"name": "value", "value": ".*"}]}]})
    try:
        a = search(name, movie)
        has = lambda r: any(f["id"] == cf["id"] for f in r["customFormats"])
        # Releases rejected before custom-format evaluation (unknown movie, unparsable) carry none.
        evaluated = [r for r in a if r.get("mappedMovieId")]
        matched = [r for r in a if has(r)]
        summary["evaluated_releases"] = len(evaluated)
        summary["new_format_matched_all_evaluated"] = len(matched) == len(evaluated) > 0
        m = api(name, "GET", f"/api/v3/movie/{movie}")
        profile = api(name, "GET", f"/api/v3/qualityprofile/{m['qualityProfileId']}")
        original = json.loads(json.dumps(profile))
        try:
            for item in profile["formatItems"]:
                if item["format"] == cf["id"]:
                    item["score"] = 7
            api(name, "PUT", f"/api/v3/qualityprofile/{profile['id']}", profile)
            b = search(name, movie)
            before = {(r["guid"], r["indexerId"]): r["customFormatScore"] for r in matched}
            summary["profile_score_edit_immediate"] = all(
                r["customFormatScore"] == before[(r["guid"], r["indexerId"])] + 7
                for r in b if (r["guid"], r["indexerId"]) in before)
        finally:
            api(name, "PUT", f"/api/v3/qualityprofile/{profile['id']}", original)
        cf["specifications"][0]["fields"][0]["value"] = "(?!)"
        api(name, "PUT", f"/api/v3/customformat/{cf['id']}", cf)
        c = search(name, movie)
        summary["format_edit_immediate"] = not any(has(r) for r in c)
    finally:
        api(name, "DELETE", f"/api/v3/customformat/{cf['id']}")
    d = search(name, movie)
    summary["format_delete_immediate"] = not any(any(f["id"] == cf["id"] for f in r["customFormats"]) for r in d)
    print(json.dumps(summary))
    sys.exit(0 if all(v for k, v in summary.items() if k != "evaluated_releases") else 1)


if __name__ == "__main__":
    main()
