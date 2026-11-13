"""Small shape corpus for the optional real-source export failure diagnostic."""
import copy

PREFIX = "source-material-failure-"
OWNER = {"path": "lyrics.style", "region": "stroke", "slot": 0,
         "sourceMaterial": {"packageId": "material", "revision": "sha256:" + "a" * 64, "exportId": "fill"},
         "parametersJson": "{}"}
BUDGET = {"currentSurfaces": 256, "additionalSurfaces": 1, "maximumSurfaces": 256,
          "currentBytes": "231789600", "additionalBytes": "8294400", "maximumBytes": "536870912"}
FAILURE = {"owner": OWNER, "stage": "sourceMaterial", "timeUs": "46033333", "code": "ImageSurfaceBudgetExceeded", "budget": BUDGET}
DIAGNOSTIC = {"code": "ImageSurfaceBudgetExceeded", "severity": "error", "message": "源材质执行超过中间表面预算，导出已停止。",
              "originKind": "sourceMaterialFailure", "sourceMaterialFailure": FAILURE}


def fixtures():
    cases = []

    def add(name, schema, value, valid=True):
        cases.append({"name": PREFIX + name, "schema": schema, "value": copy.deepcopy(value), "valid": valid})

    add("public-budget", "BackendDiagnosticDto", DIAGNOSTIC)
    raw = {**DIAGNOSTIC, **{field: None for field in ("location", "occurrence", "source", "imageEffect", "material")}}
    add("raw-budget", "BackendDiagnosticDtoRaw", raw)
    add("runtime-occurrence", "BackendDiagnosticDto", {**DIAGNOSTIC, "occurrence": {}}, False)
    add("byte-number", "BackendImageSurfaceBudgetDto", {**BUDGET, "currentBytes": 231789600}, False)
    return cases
