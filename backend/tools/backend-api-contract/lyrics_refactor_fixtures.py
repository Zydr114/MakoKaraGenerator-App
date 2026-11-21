"""Contract 1.59 lyrics beat-strike, line split/merge, non-destructive ruby, and text-diff fixtures."""
import copy

PREFIX = "lyrics-refactor-"


def fixtures():
    cases = []

    def add(name, schema, value, valid=True):
        cases.append(dict(name=PREFIX + name, schema=schema, value=copy.deepcopy(value), valid=valid))

    token_beat = dict(branchId="primary", lineId="line-1", tokenId="tok-1")
    ruby_beat = dict(branchId="primary", lineId="line-1", tokenId="tok-2", rubyId="ruby-1", segmentId="seg-1")
    line_target = dict(branchId="primary", lineId="line-1")
    token_target = dict(branchId="primary", lineId="line-1", tokenId="tok-1")

    add("strike-beat-open-only", "ProjectCommandDtoInput", dict(
        type="lyrics.strikeBeat", branchId="primary", openTarget=token_beat, playheadUs="1200000"), True)
    add("strike-beat-close-and-open-ruby", "ProjectCommandDtoInput", dict(
        type="lyrics.strikeBeat", branchId="primary", closeTarget=token_beat, openTarget=ruby_beat,
        playheadUs="1800000", closeGuardGapUs="1500000", fallbackDurationUs="600000"), True)
    add("strike-beat-missing-open", "ProjectCommandDtoInput", dict(
        type="lyrics.strikeBeat", branchId="primary", playheadUs="1200000"), False)
    add("strike-beat-extra-field", "ProjectCommandDtoInput", dict(
        type="lyrics.strikeBeat", branchId="primary", openTarget=token_beat, playheadUs="1200000", extra=1), False)

    add("close-beat-token", "ProjectCommandDtoInput", dict(
        type="lyrics.closeBeat", target=token_beat, playheadUs="2000000"), True)
    add("close-beat-ruby", "ProjectCommandDtoInput", dict(
        type="lyrics.closeBeat", target=ruby_beat, playheadUs="2100000"), True)
    add("close-beat-missing-playhead", "ProjectCommandDtoInput", dict(
        type="lyrics.closeBeat", target=token_beat), False)

    add("toggle-phrase-end-hint", "ProjectCommandDtoInput", dict(
        type="lyrics.togglePhraseEndHint", target=token_target), True)
    add("toggle-phrase-end-hint-line-only", "ProjectCommandDtoInput", dict(
        type="lyrics.togglePhraseEndHint", target=line_target), False)

    add("split-line-valid", "ProjectCommandDtoInput", dict(
        type="lyrics.splitLine", target=line_target, tokenIndex=2), True)
    add("split-line-zero-index", "ProjectCommandDtoInput", dict(
        type="lyrics.splitLine", target=line_target, tokenIndex=0), False)

    add("merge-lines-valid", "ProjectCommandDtoInput", dict(
        type="lyrics.mergeLines", target=line_target), True)
    add("merge-lines-with-token", "ProjectCommandDtoInput", dict(
        type="lyrics.mergeLines", target=token_target), False)

    add("set-ruby-segments-multi", "ProjectCommandDtoInput", dict(
        type="lyrics.setRubySegments", target=token_target, segments=["ふ", "た", "り"]), True)
    add("set-ruby-segments-clear", "ProjectCommandDtoInput", dict(
        type="lyrics.setRubySegments", target=token_target, segments=[]), True)
    add("set-ruby-segments-null-item", "ProjectCommandDtoInput", dict(
        type="lyrics.setRubySegments", target=token_target, segments=[None]), False)

    add("apply-text-diff-valid", "ProjectCommandDtoInput", dict(
        type="lyrics.applyTextDiff", branchId="primary", lines=[
            dict(text="夜空の下", sourceLineId="line-1"),
            dict(text="新しい行"),
        ]), True)
    add("apply-text-diff-missing-text", "ProjectCommandDtoInput", dict(
        type="lyrics.applyTextDiff", branchId="primary", lines=[dict(sourceLineId="line-1")]), False)

    add("token-dto-phrase-end-hint", "ProjectLyricTokenDto", dict(
        id="tok-1", text="夢", time={}, timingStatus="missing", phraseEndHint=True), True)
    add("token-dto-invalid-phrase-end-hint", "ProjectLyricTokenDto", dict(
        id="tok-1", text="夢", time={}, timingStatus="missing", phraseEndHint="yes"), False)

    add("timing-result-close-guard", "LyricTimingCommandResultDto", dict(
        estimatedPlaceholder=True, closeGuardApplied=True), True)
    add("timing-result-invalid-close-guard", "LyricTimingCommandResultDto", dict(
        estimatedPlaceholder=True, closeGuardApplied="yes"), False)

    return cases
