"""P4 public author/configuration wire examples; no runtime acceptance claims."""
import copy


def fixtures():
    cases = []
    def add(name, schema, value, valid=True):
        cases.append(dict(name="p4-author-" + name, schema=schema, value=copy.deepcopy(value), valid=valid))
    revision = "sha256:" + "a" * 64
    library = "b" * 64
    draft = "draft-" + "c" * 32
    metadata = dict(requestId="p4-request", hostInstanceId="host", validUntilUtc="2026-10-25T12:00:00Z")
    configuration = dict(name="Author example", packageId="example.package", sourceRevision=revision, exportId="scale",
        parametersJson='{"exact":9007199254740993}', relativeTarget="application", readScope="boundSources", branchInput=None,
        fixedSeed="18446744073709551615", overlayLayer="package")
    add("configuration-exact-integers", "AuthorDraftConfigurationInputDtoInput", configuration)
    for field in configuration:
        add("configuration-missing-" + field, "AuthorDraftConfigurationInputDtoInput", {k:v for k,v in configuration.items() if k != field}, False)
    for field, value in (("fixedSeed", 18446744073709551615), ("parametersJson", {}), ("readScope", "unknown"),
            ("branchInput", "unused"), ("path", "/server/private")):
        add("configuration-invalid-" + field, "AuthorDraftConfigurationInputDtoInput", {**configuration, field:value}, False)
    add("configuration-branch", "AuthorDraftConfigurationInputDtoInput", {**configuration, "readScope":"branchInput", "branchInput":"translation"})
    add("configuration-branch-needs-id", "AuthorDraftConfigurationInputDtoInput", {**configuration, "readScope":"branchInput"}, False)
    request = dict(expectedDraftRevision=revision, configuration=configuration, metadata=metadata)
    add("configuration-save", "AuthorDraftConfigurationRequest", request)
    add("configuration-save-missing-metadata", "AuthorDraftConfigurationRequest", {k:v for k,v in request.items() if k != "metadata"}, False)
    compile_request = dict(expectedDraftRevision=revision, metadata=metadata)
    for name, fields in (("default", {}), ("null", {"executionMode":None}), ("sync", {"executionMode":"sync"}), ("async", {"executionMode":"async"})):
        add("compilation-" + name, "AuthorCompilationRequest", {**compile_request, **fields})
    for name, fields in (("unknown", {"executionMode":"background"}), ("wrong-case", {"executionMode":"Async"}),
            ("worker-path", {"workerPath":"/server/tool"})):
        add("compilation-reject-" + name, "AuthorCompilationRequest", {**compile_request, **fields}, False)
    validation = dict(expectedDraftRevision=revision, sampleId="workshop-single-line-v1", targetKey="line:0", metadata=metadata)
    for sample in ("workshop-single-line-v1", "workshop-multi-line-v1"):
        add("validation-" + sample, "AuthorValidationJobRequest", {**validation, "sampleId":sample})
    for field, value in (("sampleId","invented"),("materialSlot","invented"),("targetKey",None),
            ("projectSessionId","project"),("framesPerOutput",4),("path","/server/private")):
        add("validation-reject-" + field, "AuthorValidationJobRequest", {**validation, field:value}, False)
    for field in ("expectedDraftRevision","sampleId","targetKey","metadata"):
        add("validation-missing-"+field,"AuthorValidationJobRequest",{k:v for k,v in validation.items() if k!=field},False)

    job = dict(jobId="job-author", kind="author-compilation", state="queued", expiresAtUtc="2026-10-25T13:00:00Z",
        cancellationRequested=False, canCancel=True,
        authorCompilationInput=dict(draftId=draft, draftRevision=revision, sourceRevision=revision))
    add("compilation-job", "JobResponse", job)
    validation_input = dict(sourceKind="authorDraft", sourceId=draft, sourceRevision=revision,
        sourceProfile="author-script-sample-v1", sourceSha256="d"*64, framesPerOutput=360,
        canvasWidth=640, canvasHeight=360, frameRateNumerator=30, frameRateDenominator=1, outputDurationUs="12000000")
    author_identity = dict(draftId=draft, draftRevision=revision, sourceRevision=revision,
        sampleId="workshop-single-line-v1", targetKey="token", materialSlot=None)
    add("validation-job", "JobResponse", {**{k:v for k,v in job.items() if k != "authorCompilationInput"},
        "kind": "validation", "input": validation_input,
        "authorValidationInput": author_identity})
    for schema in ("BackendJobInputDto", "BackendJobInputDtoRaw"):
        add("validation-input-"+schema, schema, validation_input)
        add("validation-input-decimal-reject-"+schema, schema, {**validation_input, "sourceRevision":"1"}, False)
        add("validation-input-project-profile-reject-"+schema, schema,
            {**validation_input, "sourceProfile":"runtime-project-input-v1"}, False)
        project_input = {**validation_input, "sourceKind":"project", "sourceId":"project-1", "sourceRevision":"1",
            "sourceProfile":"runtime-project-input-v1"}
        add("validation-project-input-"+schema, schema, project_input)
        add("validation-project-sha-reject-"+schema, schema, {**project_input, "sourceRevision":revision}, False)
        add("validation-project-author-profile-reject-"+schema, schema,
            {**project_input, "sourceProfile":"author-script-sample-v1"}, False)

    author_source = dict(kind="authorDraft", sourceId=draft, sourceRevision=revision,
        authorSourceRevision=revision,
        templateSample=dict(sampleId="workshop-single-line-v1", targetKey="token", materialSlot=None))
    author_preview = dict(source=author_source, size=dict(width=640, height=360), alphaMode="opaque",
        purpose="preview", metadata=metadata, previewKind="authorDraft", committable=False)
    add("preview-author-draft", "PreviewSessionRequest", author_preview)
    add("preview-author-draft-multi-line", "PreviewSessionRequest", {
        **author_preview, "source": {**author_source, "templateSample": {
            "sampleId": "workshop-multi-line-v1", "targetKey": "line:0", "materialSlot": "sungFill"}}})
    for name, source in (
        ("missing-source-revision", {k: v for k, v in author_source.items() if k != "authorSourceRevision"}),
        ("missing-sample", {k: v for k, v in author_source.items() if k != "templateSample"}),
        ("default-sample", {**author_source, "templateSample": {"sampleId": "default-v1", "targetKey": "token"}}),
        ("project-extra-author-fields", {"kind": "project", "sourceId": "project-1", "sourceRevision": "1",
            "authorSourceRevision": revision, "templateSample": None}),
    ):
        add("preview-author-reject-" + name, "PreviewSessionRequest", {**author_preview, "source": source}, False)
    update = dict(expectedPreviewGeneration="1", expectedSourceRevision=revision, source=author_source,
        size=dict(width=640, height=360), alphaMode="opaque", metadata=metadata)
    add("preview-author-update", "PreviewProjectInputRequest", update)
    add("preview-author-update-missing-target", "PreviewProjectInputRequest", {
        **update, "source": {**author_source, "templateSample": {"sampleId": "workshop-single-line-v1"}}}, False)
    source = dict(draftId=draft, expectedDraftRevision=revision, sourceRevision=revision)
    config_source = dict(configurationId="configuration", expectedConfigurationRevision=library)
    asset = dict(kind="asset", packageId="example.package", packageRevision=revision, packageAssetId="asset")
    for name, value in (("draft",source),("configuration",config_source),("package",dict(package=asset))):
        add("library-source-"+name,"ScriptConfigurationSourceDtoInput",value)
        for field in value:
            add("library-source-"+name+"-missing-"+field,"ScriptConfigurationSourceDtoInput",{k:v for k,v in value.items() if k!=field},False)
    add("library-source-empty","ScriptConfigurationSourceDtoInput",{},False)
    add("library-source-mixed","ScriptConfigurationSourceDtoInput",{**source,**config_source},False)
    library_configuration = {k:v for k,v in configuration.items() if k not in ("packageId","sourceRevision")}
    library_configuration["source"] = source
    commands = [dict(type="create",configuration=library_configuration),
        dict(type="replace",configurationId="configuration",configuration=library_configuration),
        dict(type="copy",configurationId="configuration",name="Copy"),
        dict(type="rename",configurationId="configuration",name="Renamed"),
        dict(type="delete",configurationId="configuration")]
    for command in commands:
        kind=command["type"]
        add("library-command-"+kind,"ScriptConfigurationCommandDtoInput",command)
        add("library-command-"+kind+"-unknown","ScriptConfigurationCommandDtoInput",{**command,"unexpected":True},False)
        for field in command:
            add("library-command-"+kind+"-missing-"+field,"ScriptConfigurationCommandDtoInput",{k:v for k,v in command.items() if k!=field},False)
        add("library-envelope-"+kind,"ScriptConfigurationCommandRequest",dict(expectedRevision=library,metadata=metadata,command=command))
        add("library-envelope-"+kind+"-unknown","ScriptConfigurationCommandRequest",
            {"expectedRevision": library, "metadata": metadata, "command": command, "unexpected": True}, False)
    add("library-command-response-empty", "ScriptConfigurationCommandResponse", {
        "previousRevision": "missing", "revision": library, "changed": True,
        "configurationId": "configuration", "protocolVersion": "1"})
    reference = dict(kind="configuration",packageId="example.package",packageRevision=revision,
        configurationId="configuration",configurationRevision=library)
    add("package-configuration-reference","EffectPackageReferenceDtoInput",reference)
    add("package-configuration-inspect","EffectPackageInspectRequest",dict(reference=reference))
    for field in reference:
        add("package-configuration-reference-missing-"+field,"EffectPackageReferenceDtoInput",{k:v for k,v in reference.items() if k!=field},False)
    add("package-configuration-reference-mixed","EffectPackageReferenceDtoInput",{**reference,"packageAssetId":"asset"},False)
    return {"cases":cases}


def append_to(existing):
    """Idempotent append; every historical example retains name/schema/valid/value."""
    from p3_workshop_fixtures import append_to as append_p3
    base = append_p3({**existing, "cases": [case for case in existing["cases"] if not case["name"].startswith("p4-author-")]})
    return {**base, "cases": base["cases"] + fixtures()["cases"]}
