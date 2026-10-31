#!/usr/bin/env python3
"""ADR-0233 wire cases; semantic parent ownership/timing remains service acceptance."""
import copy
import json


def fixtures():
    cases = []
    def add(name, schema, value, valid=True):
        cases.append(dict(name="ruby-timeline-" + name, schema=schema, value=copy.deepcopy(value), valid=valid))
    group = dict(branchId="b", lineId="l", tokenId="t", rubyId="r")
    segment = dict(**group, segmentId="s")
    metadata = dict(requestId="ruby-request", hostInstanceId="ruby-host", validUntilUtc="2030-01-01T00:00:00Z")
    commands = [
        dict(type="lyrics.setRubySegmentTime", target=segment, startUs="9007199254740993", endUs="9223372036854775807"),
        dict(type="lyrics.clearRubySegmentTime", target=segment),
        dict(type="lyrics.setRubySegmentText", target=segment, text="せ"),
        dict(type="lyrics.addRubySegment", target=group, text="ん", afterSegmentId=None),
        dict(type="lyrics.removeRubySegment", target=segment),
        dict(type="lyrics.reorderRubySegments", target=group, orderedSegmentIds=["s", "s2"]),
        dict(type="lyrics.setRubySegmentTimes", changes=[dict(target=segment, startUs="0", endUs="9007199254740993")]),
    ]
    for command in commands:
        name = command["type"].split(".")[1]
        add(name, "ProjectCommandDtoInput", command)
        add(name + "-envelope", "ProjectCommandRequest", dict(expectedRevision="9007199254740993", command=command, **metadata))
        for field in command:
            add(name + "-missing-" + field, "ProjectCommandDtoInput", {k:v for k,v in command.items() if k != field}, False)
            if field != "afterSegmentId":
                add(name + "-null-" + field, "ProjectCommandDtoInput", {**command, field:None}, False)
        for field in ("rubyTarget", "segmentStartUs", "segmentEndUs", "unexpected", "analysisJobId"):
            add(name + "-unknown-" + field, "ProjectCommandDtoInput", {**command, field:None}, False)
        if "target" in command:
            for field in command["target"]:
                for suffix, value in (("missing", None), ("null", None), ("blank", " \t"), ("long", "x" * 129), ("number", 1)):
                    target = {**command["target"], field:value}
                    if suffix == "missing": del target[field]
                    add(name + "-target-" + field + "-" + suffix, "ProjectCommandDtoInput", {**command, "target":target}, False)
            foreign = "segmentId" if "segmentId" not in command["target"] else "unknownId"
            add(name + "-target-extra", "ProjectCommandDtoInput", {**command,"target":{**command["target"], foreign:"s"}}, False)
    for field in ("startUs", "endUs"):
        for suffix, value in (("number",9007199254740993),("null",None),("fraction","1.5"),("overflow","9223372036854775808"),("space"," 1"),("letters","1us")):
            add("set-time-" + field + "-" + suffix,"ProjectCommandDtoInput",{**commands[0],field:value},False)
    add("set-time-domain-negative", "ProjectCommandDtoInput", {**commands[0],"startUs":"-1","endUs":"1"})
    add("set-time-int64-min-domain-rejected", "ProjectCommandDtoInput", {**commands[0],"startUs":"-9223372036854775808","endUs":"1"})
    add("set-time-leading-plus", "ProjectCommandDtoInput", {**commands[0],"startUs":"+1","endUs":"+2"})
    add("set-time-leading-zero", "ProjectCommandDtoInput", {**commands[0],"startUs":"0001","endUs":"0002"})
    add("add-after", "ProjectCommandDtoInput", {**commands[3],"afterSegmentId":"s"})
    add("add-must-be-untimed", "ProjectCommandDtoInput", {**commands[3],"startUs":"0","endUs":"1"},False)
    for suffix, ids in (("empty",[]),("null-item",[None]),("blank",[" "]),("long",["x"*129])):
        add("reorder-"+suffix,"ProjectCommandDtoInput",{**commands[5],"orderedSegmentIds":ids},False)
    for size in (0,1,1000,1001):
        add("batch-count-"+str(size),"ProjectCommandDtoInput",dict(type=commands[6]["type"],changes=[dict(target={**segment,"segmentId":"s"+str(i)},startUs="0",endUs="1") for i in range(size)]),1<=size<=1000)
    entry=commands[6]["changes"][0]
    for field in entry:
        add("batch-missing-"+field,"RubySegmentTimeChangeDtoInput",{k:v for k,v in entry.items() if k!=field},False)
        add("batch-null-"+field,"RubySegmentTimeChangeDtoInput",{**entry,field:None},False)
    add("batch-extra","RubySegmentTimeChangeDtoInput",{**entry,"rubyId":"r"},False)
    add("batch-old-token-target","RubySegmentTimeChangeDtoInput",{**entry,"target":dict(branchId="b",lineId="l",tokenId="t")},False)
    base=dict(expectedRevision="0",format="ttml.project",options={},metadata=metadata)
    add("document-plan","LyricsExportPlanRequest",dict(**base,scope="document"))
    add("branch-plan-default","LyricsExportPlanRequest",dict(**base,branchId="b"))
    add("branch-plan-explicit","LyricsExportPlanRequest",dict(**base,branchId="b",scope="branch"))
    for suffix, values in (("branch-id",dict(scope="document",branchId="b")),("null-branch",dict(scope="document",branchId=None)),("missing-scope",{}),("null-scope",dict(scope=None)),("unknown-scope",dict(scope="project"))):
        add("plan-"+suffix,"LyricsExportPlanRequest",{**base,**values},False)
    for fmt in ("lrc.plain","lrc.enhanced","ttml.amll"):
        add("document-format-"+fmt,"LyricsExportPlanRequest",{**base,"scope":"document","format":fmt},False)
    for value,valid,name in (("b",True,"selected"),(None,False,"null"),(" ",False,"blank"),("x"*129,False,"long"),(1,False,"number")):
        add("import-source-"+name,"LyricsImportOptionsDtoInput",dict(sourceBranchId=value),valid)
    add("import-source-omitted","LyricsImportOptionsDtoInput",{})
    ruby=dict(id="r",baseTokenId="t",text="せん",relativeScale=.5,gapPx=0,alignment="center",segments=[dict(id="s",text="せ",time=dict(startUs="9007199254740993",endUs="9007199254740994"),timingStatus="imported"),dict(id="s2",text="ん",time={},timingStatus="missing")])
    add("read-ruby","ProjectRubyAnnotationDto",ruby)
    add("read-no-segments","ProjectRubyAnnotationDto",{k:v for k,v in ruby.items() if k!="segments"},False)
    for field in ruby["segments"][0]:
        add("read-segment-missing-"+field,"ProjectRubyTextSegmentDto",{k:v for k,v in ruby["segments"][0].items() if k!=field},False)
    add("read-segment-number-time","ProjectRubyTextSegmentDto",{**ruby["segments"][0],"time":dict(startUs=9007199254740993)},False)
    for name,value in (("timing",dict(changedSegmentCount=1,selectionChanged=False,target=group,selectedSegmentId="s")),("added",dict(changedSegmentCount=1,selectionChanged=True,target=group,selectedSegmentId="new",createdSegmentId="new")),("deleted-last",dict(changedSegmentCount=1,selectionChanged=True,target=group)),("batch",dict(changedSegmentCount=2,selectionChanged=False)),("no-op",dict(changedSegmentCount=0,selectionChanged=False))):
        add("result-"+name,"RubySegmentCommandResultDto",value)
        for field in ("changedSegmentCount","selectionChanged"):
            add("result-"+name+"-missing-"+field,"RubySegmentCommandResultDto",{k:v for k,v in value.items() if k!=field},False)
    add("result-negative-count","RubySegmentCommandResultDto",dict(changedSegmentCount=-1,selectionChanged=False),False)
    assessment=dict(losses=[],exportableLineCount=2,skippedLineCount=0,issues=[],hasLosses=False)
    options=dict(newLine="\n",writeByteOrderMark=False,millisecondPrecision=False)
    plan=dict(planId="p",projectSessionId="session",projectRevision="9007199254740993",branchId=None,format="ttml.project",options=options,encoding="utf-8",expiresAtUtc="2030-01-01T00:00:00Z",plan=assessment,additionalLosses=[],requiresLossAcceptance=False,omittedBranchCount=0,scope="document",protocolVersion="1")
    report=dict(exporterId="ttml.project",exporterName="TTML",sourceLineCount=2,exportedLineCount=2,skippedLineCount=0,droppedTokenTimeCount=0,roundedTimeCount=0,writtenByteCount=1024,metadataCount=0,plan=assessment,writtenRubySegmentCount=2,skippedRubySegmentCount=0,droppedRubySegmentTimeCount=0)
    exported=dict(exportId="e",planId="p",projectSessionId="session",projectRevision="9007199254740993",branchId=None,format="ttml.project",encoding="utf-8",sizeBytes=1024,sha256="a"*64,contentUrl="/api/v1/lyrics-exports/e/content",expiresAtUtc="2030-01-01T00:00:00Z",report=report,additionalLosses=[],scope="document",protocolVersion="1")
    for schema,value in (("LyricsExportPlanResponse",plan),("LyricsExportResponse",exported)):
        add("document-response-"+schema,schema,value)
        for field in ("branchId","scope"):
            add("document-response-"+schema+"-missing-"+field,schema,{k:v for k,v in value.items() if k!=field},False)
        add("document-response-"+schema+"-branch-id",schema,{**value,"branchId":"b"},False)
        add("document-response-"+schema+"-amll",schema,{**value,"format":"ttml.amll"},False)
        add("branch-response-"+schema+"-null-id",schema,{**value,"scope":"branch"},False)
        add("branch-response-"+schema,schema,{**value,"scope":"branch","branchId":"b"})
    for field in ("writtenRubySegmentCount","skippedRubySegmentCount","droppedRubySegmentTimeCount"):
        add("export-report-missing-"+field,"LyricsExportReportDto",{k:v for k,v in report.items() if k!=field},False)
        add("export-report-negative-"+field,"LyricsExportReportDto",{**report,field:-1},False)
    add("read-invalid-raw-time","ProjectRubyTextSegmentDto",dict(id="invalid",text="せ",time=dict(startUs="-1",endUs="0"),timingStatus="invalid"))
    return {"cases":cases}


if __name__ == "__main__":
    print(json.dumps(fixtures(),ensure_ascii=False,indent=2))
