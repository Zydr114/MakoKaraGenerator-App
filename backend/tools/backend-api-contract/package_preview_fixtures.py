"""P2-A synthetic public wire cases; source ownership/bytes/leases remain runtime gates."""
import copy
import json


def fixtures():
    cases = []
    def add(name, schema, value, valid=True):
        cases.append(dict(name="package-preview-" + name, schema=schema, value=copy.deepcopy(value), valid=valid))
    identity = dict(packageId="package", revision="sha256:"+"a"*64, exportId="export")
    source = dict(packageId="package", revision=identity["revision"], instanceId="instance", exportId="export", code="CS1002", message="Expected semicolon", isError=True, path="effects/main.cs", line=0, column=0)
    image = dict(instanceId="instance", artifactHash="b"*64, graph=identity, inputId="background", code="BackgroundUnavailable")
    script = dict(kind="script", script=dict(instanceId="instance", artifactHash="sha256:"+"b"*64, packageIdentity=identity))
    source_owner = dict(kind="source", source=dict(path="lyrics.style",region="fill",slot=0,sourceMaterial=identity,parametersJson='{"amount":1}'))
    material = dict(owner=script, material=identity, inputId="background", code="BackgroundUnavailable")
    values = [dict(code=source["code"], severity="error", message=source["message"], originKind="compileSource",source=source),
              dict(code=image["code"],severity="warning",message="图效果使用了已声明的降级行为。",originKind="imageEffect",imageEffect=image),
              dict(code=material["code"],severity="warning",message="材质使用了已声明的降级行为。",originKind="material",material=material)]
    for schema, value in (("BackendSourceDiagnosticDto",source),("BackendDiagnosticResourceDto",identity),("BackendImageEffectDiagnosticDto",image),("BackendMaterialDiagnosticDto",material),("BackendDiagnosticMaterialOwnerDto",script),("BackendDiagnosticScriptOwnerDto",script["script"]),("BackendDiagnosticSourceOwnerDto",source_owner["source"])):
        add("complete-"+schema,schema,value)
        optional = {"instanceId","exportId","path"} if schema=="BackendSourceDiagnosticDto" else {"packageIdentity"} if schema=="BackendDiagnosticScriptOwnerDto" else set()
        required = [field for field in value if field not in optional]
        for field in required:
            add(schema+"-missing-"+field,schema,{k:v for k,v in value.items() if k!=field},False)
            add(schema+"-null-"+field,schema,{**value,field:None},False)
    for d in values:
        kind=d["originKind"];payload={"compileSource":"source","imageEffect":"imageEffect","material":"material"}[kind]
        add(kind,"BackendDiagnosticDto",d)
        for field in ("originKind",payload):
            add(kind+"-missing-"+field,"BackendDiagnosticDto",{k:v for k,v in d.items() if k!=field},False)
            add(kind+"-null-"+field,"BackendDiagnosticDto",{**d,field:None},False)
        for foreign in ("source","imageEffect","material","occurrence","location"):
            if foreign!=payload:
                add(kind+"-foreign-"+foreign,"BackendDiagnosticDto",{**d,foreign:{} if foreign!="location" else "server/path"},False)
        add(kind+"-wrong-severity","BackendDiagnosticDto",{**d,"severity":"info"},False)
        add(kind+"-unknown-origin","BackendDiagnosticDto",{**d,"originKind":"unknown"},False)
    add("source-warning","BackendDiagnosticDto",{**values[0],"severity":"warning","source":{**source,"isError":False}})
    add("source-error-wrong-severity","BackendDiagnosticDto",{**values[0],"severity":"warning"},False)
    add("source-warning-wrong-severity","BackendDiagnosticDto",{**values[0],"source":{**source,"isError":False}},False)
    add("source-unassociated","BackendSourceDiagnosticDto",{k:v for k,v in source.items() if k not in ("instanceId","exportId","path")})
    for field in ("instanceId","exportId"):
        add("source-half-associated-"+field,"BackendSourceDiagnosticDto",{k:v for k,v in source.items() if k!=field},False)
    for field in ("line","column"):
        for value in (-1,0,1,2147483647,2147483648):
            add("source-"+field+"-"+str(value),"BackendSourceDiagnosticDto",{**source,field:value},0<=value<=2147483647)
    for name,value in (("script",script),("source",source_owner),("script-no-package",dict(kind="script",script={k:v for k,v in script["script"].items() if k!="packageIdentity"}))):
        add("owner-"+name,"BackendDiagnosticMaterialOwnerDto",value)
        add("material-"+name,"BackendDiagnosticDto",{**values[2],"material":{**material,"owner":value}})
    for value in (dict(kind="script"),dict(kind="source"),dict(kind="unknown",source=source_owner["source"]),dict(**script,source=source_owner["source"]),dict(kind="script",script=None),dict(kind="source",source=None)):
        add("owner-invalid-"+str(len(cases)),"BackendDiagnosticMaterialOwnerDto",value,False)
    for slot in (-1,0,1,2,3):
        add("source-owner-slot-"+str(slot),"BackendDiagnosticSourceOwnerDto",{**source_owner["source"],"slot":slot},0<=slot<=2)
    for schema,value,field in (("BackendDiagnosticResourceDto",identity,"revision"),("BackendImageEffectDiagnosticDto",image,"artifactHash")):
        for bad in ("x","a"*63,"sha256:"+"z"*64):
            add(schema+"-bad-"+field+"-"+str(len(cases)),schema,{**value,field:bad},False)
    page=dict(previewId="preview",previewGeneration="1",sourceRevision="2",diagnostics=values,hasMore=False,protocolVersion="1")
    add("page-three-kinds","PreviewDiagnosticsPageDto",page)
    for size in (0,1,128,129):
        add("page-count-"+str(size),"PreviewDiagnosticsPageDto",{**page,"diagnostics":[values[0]]*size},size<=128)
    add("legacy-load-report","BackendDiagnosticDto",dict(code="Saved",severity="Info",message="saved"))
    add("legacy-transport","BackendDiagnosticDto",dict(code="WorkerUnavailable",severity="error",message="Unavailable",originKind="transportFailure"))
    return {"cases":cases}


if __name__ == "__main__":
    print(json.dumps(fixtures(),ensure_ascii=False,indent=2))
