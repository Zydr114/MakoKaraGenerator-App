"""P3 public wire examples. Identity, CAS, leases and rendering remain service gates."""
import copy


def fixtures():
    from template_library_fixtures import static_definition
    cases = []
    def add(name, schema, value, valid=True):
        cases.append(dict(name="p3-workshop-" + name, schema=schema, value=copy.deepcopy(value), valid=valid))
    revision = "a" * 64
    package_revision = "sha256:" + "b" * 64
    asset = dict(kind="asset", packageId="demo.closed", packageRevision=package_revision, packageAssetId="asset")
    library = dict(kind="library", packageId="demo.closed", packageRevision=package_revision, libraryId="personal", libraryRevision=revision, templateId="template", templateRevision=revision)
    for name, reference in (("asset", asset), ("library", library)):
        add("reference-" + name, "EffectPackageReferenceDtoInput", reference)
        for field in reference:
            add("reference-" + name + "-missing-" + field, "EffectPackageReferenceDtoInput", {k:v for k,v in reference.items() if k != field}, False)
        add("reference-" + name + "-path", "EffectPackageReferenceDtoInput", {**reference, "path": "/server/path"}, False)
    add("inspect-asset", "EffectPackageInspectRequest", dict(packageAssetId="asset", expectedPackageRevision=package_revision))
    add("inspect-library", "EffectPackageInspectRequest", dict(reference=library))
    add("inspect-empty", "EffectPackageInspectRequest", {}, False)
    add("inspect-mixed", "EffectPackageInspectRequest", dict(packageAssetId="asset", reference=library), False)
    add("inspect-reference-extra-revision", "EffectPackageInspectRequest", dict(reference=library, expectedPackageRevision=package_revision), False)
    parameter = dict(id="whole", type="integer", label="Whole", defaultValueJson="9007199254740993", minimumJson="0", maximumJson="9223372036854775807", options=[])
    add("parameter-exact-int64", "EffectPackageParameterSchemaDto", parameter)
    add("parameter-json-number-rejected", "EffectPackageParameterSchemaDto", {**parameter, "defaultValueJson": 9007199254740993}, False)
    recipe = dict(recipeId="r1", name="Scale", target="tokenFamily", packageId="demo.closed", revision=package_revision,
        exportId="scale", parametersJson='{"peak":1.5,"exact":9007199254740993}', overlayLayer="package", enabled=True,
        readScope="boundSources", branchInput=None, fixedSeed="18446744073709551615", configurationId=None)
    definition = {**static_definition(), "effectRecipes": [recipe], "packages": [asset]}
    add("full-definition", "FullTemplateDefinitionDtoInput", definition)
    add("full-pure-recipe", "FullTemplateDefinitionDtoInput", {**definition, "properties": []})
    add("full-no-op-empty", "FullTemplateDefinitionDtoInput", {**definition, "properties": [], "effectRecipes": []}, False)
    for field in ("effectRecipes", "packages", "style"):
        add("full-missing-" + field, "FullTemplateDefinitionDtoInput", {k:v for k,v in definition.items() if k != field}, False)
    add("full-null-recipe", "FullTemplateDefinitionDtoInput", {**definition, "effectRecipes": [None]}, False)
    add("full-null-reference", "FullTemplateDefinitionDtoInput", {**definition, "packages": [None]}, False)
    add("recipe-exact-fields", "TemplateEffectRecipeDto", recipe)
    for field in ("parametersJson", "fixedSeed", "branchInput", "configurationId"):
        add("recipe-missing-" + field, "TemplateEffectRecipeDto", {k:v for k,v in recipe.items() if k != field}, False)
    add("recipe-number-seed", "TemplateEffectRecipeDto", {**recipe, "fixedSeed": 18446744073709551615}, False)
    add("recipe-branch-input", "TemplateEffectRecipeDto", {**recipe, "readScope": "branchInput", "branchInput": "translation"})
    add("recipe-missing-branch", "TemplateEffectRecipeDto", {**recipe, "readScope": "branchInput"}, False)
    material = dict(kind="package", packageId="demo.closed", revision=package_revision, exportId="ink", parametersJson='{"exact":9007199254740993}')
    add("full-package-material", "FullTemplatePackageMaterial", material)
    add("full-package-material-no-parameters", "FullTemplatePackageMaterial", {k:v for k,v in material.items() if k != "parametersJson"}, False)
    advanced = copy.deepcopy(definition); advanced["style"]["unsungFill"]["material"] = material
    add("full-unselected-material", "FullTemplateDefinitionDtoInput", advanced)
    add("legacy-reject-full-closure", "StaticTemplateDefinitionDtoInput", definition, False)
    for kind in ("create", "replace"):
        command = dict(type=kind, definitionFormat="full", definition=definition)
        if kind == "replace": command["templateId"] = "template"
        add("command-" + kind + "-full", "TemplateLibraryCommandDtoInput", command)
        add("command-" + kind + "-full-needs-marker", "TemplateLibraryCommandDtoInput", {k:v for k,v in command.items() if k != "definitionFormat"}, False)
        add("command-" + kind + "-full-wrong-marker", "TemplateLibraryCommandDtoInput", {**command, "definitionFormat": "static"}, False)
    source = dict(kind="draft", draftRevision="draft-1", definitionFormat="full", definition=definition)
    saved = dict(kind="savedTemplate", libraryId="personal", libraryRevision=revision, templateId="template", templateRevision=revision)
    for name, value in (("saved", saved), ("full", source), ("static", dict(kind="draft", draftRevision="draft-1", definition=static_definition()))):
        add("options-" + name, "TemplatePreviewOptionsRequest", dict(source=value))
    add("source-mixed", "TemplatePreviewOptionsRequest", dict(source={**source, "libraryId": "personal"}), False)
    add("source-saved-full-marker", "TemplatePreviewOptionsRequest", dict(source={**saved, "definitionFormat": "full"}), False)
    metadata = dict(requestId="p3-request", hostInstanceId="host", validUntilUtc="2026-10-25T12:00:00Z")
    session = dict(source=source, sampleId="workshop-multi-line-v1", size=dict(width=640,height=360), alphaMode="opaque", metadata=metadata, targetKey="line:0", materialSlot="sungFill")
    add("preview-full-workshop", "TemplatePreviewSessionRequest", session)
    add("preview-old-default", "TemplatePreviewSessionRequest", {**session, "source": saved, "sampleId": "default-v1"})
    add("preview-unknown-sample", "TemplatePreviewSessionRequest", {**session, "sampleId": "client-invented"}, False)
    add("preview-unknown-slot", "TemplatePreviewSessionRequest", {**session, "materialSlot": "other"}, False)
    clips = dict(kind="clips", branchId="primary", clipIds=["clip-1", "clip-2"])
    for name,value in (("clips",clips),("node",dict(kind="node",nodeId="node",includeDescendants=True))):
        add("target-"+name,"PresetApplicationTargetDtoInput",value)
    for name,value in (("empty",[]),("duplicate",["one","one"]),("null",None),("non-text",[1]),("too-many",[str(i) for i in range(1001)])):
        add("clips-"+name,"PresetApplicationTargetDtoInput",{**clips,"clipIds":value},False)
    add("clips-mixed-node","PresetApplicationTargetDtoInput",{**clips,"nodeId":"node"},False)
    candidate = dict(expectedProjectRevision="1", source=source, targets=[clips], size=dict(width=640,height=360), metadata=metadata)
    add("candidate-full-draft","PresetApplicationPreviewRequest",candidate)
    add("candidate-mixed-library","PresetApplicationPreviewRequest",{**candidate,"templateLibraryId":"personal"},False)
    legacy_candidate = {**candidate, "templateLibraryId":"personal", "templateLibraryRevision":revision,"templateId":"template","templateRevision":revision}
    legacy_candidate.pop("source")
    add("candidate-legacy-library","PresetApplicationPreviewRequest",legacy_candidate)
    return {"cases":cases}


def append_to(existing):
    """Deterministic idempotent source generation; historical cases keep every value."""
    additions = fixtures()["cases"]
    return {**existing, "cases": [case for case in existing["cases"] if not case["name"].startswith("p3-workshop-")] + additions}
