#!/usr/bin/env python3
"""S3d import/append acceptance over real HTTP, using only probe-owned resources."""

import argparse
import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
from urllib.parse import quote
import wave

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('s3c_token_probe', HERE / 'token_probe.py')
token = importlib.util.module_from_spec(spec)
spec.loader.exec_module(token)
content = token.content
save_json = token.save_json
TYPES = ('lyrics.importAsset', 'lyrics.appendText')
REPORT_FIELDS = set(('importerId importerName sourceFormat sourceLineCount lineCount tokenCount timedTokenCount '
    'missingTokenCount estimatedTokenCount skippedLineCount mergedContinuationCount derivedLineEndCount '
    'fallbackLineEndCount offsetUs encodingName hadByteOrderMark newLineKind issues detectionReason candidateNames '
    'isFormatAmbiguous discardedFeatures splitLineCount splitCreatedTokenCount splitEstimatedTokenCount issueCount maxSeverity').split())
COUNTS = ('sourceLineCount', 'lineCount', 'tokenCount', 'timedTokenCount', 'missingTokenCount', 'estimatedTokenCount',
          'skippedLineCount', 'mergedContinuationCount', 'derivedLineEndCount', 'fallbackLineEndCount',
          'splitLineCount', 'splitCreatedTokenCount', 'splitEstimatedTokenCount', 'issueCount')
ISSUES = ('unknownTag', 'malformedTag', 'textWithoutTimestamp', 'emptyText', 'duplicateTimestamp', 'outOfOrder',
          'negativeTimeClamped', 'wordTimeOutsideLine', 'wordTimesNotIncreasing', 'encodingAssumed', 'lineEndFallback',
          'discardedFeature', 'formatAmbiguous', 'tokenSplitApplied', 'tokenTextMismatch')

# Fixed complete reports, projected from the independent unchanged-business oracle.
FORMAT_REPORTS = json.loads(r'''
{
  "lrc.plain": {
    "importerId": "lrc.plain",
    "importerName": "普通 LRC（仅行级时间）",
    "sourceFormat": "普通 LRC 行时间标签 [mm:ss.xx]",
    "sourceLineCount": 2,
    "lineCount": 1,
    "tokenCount": 2,
    "timedTokenCount": 0,
    "missingTokenCount": 2,
    "estimatedTokenCount": 0,
    "skippedLineCount": 0,
    "mergedContinuationCount": 0,
    "derivedLineEndCount": 0,
    "fallbackLineEndCount": 0,
    "offsetUs": "0",
    "encodingName": "utf-8",
    "hadByteOrderMark": false,
    "newLineKind": "lineFeed",
    "issues": [],
    "detectionReason": "扩展名 .lrc，且文件里没有逐字时间标签 <mm:ss.xx>，因此按行级时间导入",
    "candidateNames": [
      "普通 LRC（仅行级时间）"
    ],
    "isFormatAmbiguous": false,
    "discardedFeatures": [],
    "splitLineCount": 0,
    "splitCreatedTokenCount": 0,
    "splitEstimatedTokenCount": 0,
    "issueCount": 0
  },
  "lrc.enhanced": {
    "importerId": "lrc.enhanced",
    "importerName": "增强 LRC（含逐字时间）",
    "sourceFormat": "增强 LRC 逐字标签 <mm:ss.xx>",
    "sourceLineCount": 2,
    "lineCount": 1,
    "tokenCount": 2,
    "timedTokenCount": 2,
    "missingTokenCount": 0,
    "estimatedTokenCount": 0,
    "skippedLineCount": 0,
    "mergedContinuationCount": 0,
    "derivedLineEndCount": 0,
    "fallbackLineEndCount": 0,
    "offsetUs": "0",
    "encodingName": "utf-8",
    "hadByteOrderMark": false,
    "newLineKind": "lineFeed",
    "issues": [
      {
        "kind": "formatAmbiguous",
        "severity": "info",
        "sourceLineNumber": 0,
        "rawText": "song.lrc",
        "message": "2 个导入器都能处理这个文件，已按优先级选择「增强 LRC（含逐字时间）」：文件里含逐字时间标签 <mm:ss.xx>，按增强 LRC 导入（优先于普通 LRC）"
      }
    ],
    "detectionReason": "文件里含逐字时间标签 <mm:ss.xx>，按增强 LRC 导入（优先于普通 LRC）",
    "candidateNames": [
      "增强 LRC（含逐字时间）",
      "普通 LRC（仅行级时间）"
    ],
    "isFormatAmbiguous": true,
    "discardedFeatures": [],
    "splitLineCount": 0,
    "splitCreatedTokenCount": 0,
    "splitEstimatedTokenCount": 0,
    "issueCount": 1,
    "maxSeverity": "info"
  },
  "text.plain": {
    "importerId": "text.plain",
    "importerName": "纯文本（未打轴）",
    "sourceFormat": "纯文本，每个非空行作为一行歌词",
    "sourceLineCount": 2,
    "lineCount": 1,
    "tokenCount": 2,
    "timedTokenCount": 0,
    "missingTokenCount": 2,
    "estimatedTokenCount": 0,
    "skippedLineCount": 1,
    "mergedContinuationCount": 0,
    "derivedLineEndCount": 0,
    "fallbackLineEndCount": 0,
    "offsetUs": "0",
    "encodingName": "utf-8",
    "hadByteOrderMark": false,
    "newLineKind": "lineFeed",
    "issues": [
      {
        "kind": "formatAmbiguous",
        "severity": "info",
        "sourceLineNumber": 0,
        "rawText": "song.txt",
        "message": "2 个导入器都能处理这个文件，已按优先级选择「纯文本（未打轴）」：扩展名 .txt，未识别到时间标签，按未打轴文本导入"
      }
    ],
    "detectionReason": "扩展名 .txt，未识别到时间标签，按未打轴文本导入",
    "candidateNames": [
      "纯文本（未打轴）",
      "普通 LRC（仅行级时间）"
    ],
    "isFormatAmbiguous": true,
    "discardedFeatures": [],
    "splitLineCount": 0,
    "splitCreatedTokenCount": 0,
    "splitEstimatedTokenCount": 0,
    "issueCount": 1,
    "maxSeverity": "info"
  },
  "srt": {
    "importerId": "srt",
    "importerName": "SRT 字幕（仅行级时间）",
    "sourceFormat": "SubRip 字幕块 hh:mm:ss,mmm --> hh:mm:ss,mmm",
    "sourceLineCount": 4,
    "lineCount": 1,
    "tokenCount": 2,
    "timedTokenCount": 0,
    "missingTokenCount": 2,
    "estimatedTokenCount": 0,
    "skippedLineCount": 0,
    "mergedContinuationCount": 0,
    "derivedLineEndCount": 0,
    "fallbackLineEndCount": 0,
    "offsetUs": "0",
    "encodingName": "utf-8",
    "hadByteOrderMark": false,
    "newLineKind": "lineFeed",
    "issues": [],
    "detectionReason": "扩展名为 .srt，按 SubRip 字幕块解析",
    "candidateNames": [
      "SRT 字幕（仅行级时间）"
    ],
    "isFormatAmbiguous": false,
    "discardedFeatures": [],
    "splitLineCount": 0,
    "splitCreatedTokenCount": 0,
    "splitEstimatedTokenCount": 0,
    "issueCount": 0
  },
  "vtt": {
    "importerId": "vtt",
    "importerName": "WebVTT 字幕（可含逐字）",
    "sourceFormat": "WebVTT 字幕块 hh:mm:ss.mmm --> hh:mm:ss.mmm；cue 内的 <00:00.000> 视为逐字时间",
    "sourceLineCount": 5,
    "lineCount": 1,
    "tokenCount": 2,
    "timedTokenCount": 0,
    "missingTokenCount": 2,
    "estimatedTokenCount": 0,
    "skippedLineCount": 0,
    "mergedContinuationCount": 0,
    "derivedLineEndCount": 0,
    "fallbackLineEndCount": 0,
    "offsetUs": "0",
    "encodingName": "utf-8",
    "hadByteOrderMark": false,
    "newLineKind": "lineFeed",
    "issues": [],
    "detectionReason": "扩展名为 .vtt，按 WebVTT 字幕块解析",
    "candidateNames": [
      "WebVTT 字幕（可含逐字）"
    ],
    "isFormatAmbiguous": false,
    "discardedFeatures": [],
    "splitLineCount": 0,
    "splitCreatedTokenCount": 0,
    "splitEstimatedTokenCount": 0,
    "issueCount": 0
  },
  "ass": {
    "importerId": "ass",
    "importerName": "ASS / SSA 卡拉 OK 字幕（可含逐字）",
    "sourceFormat": "ASS 事件行 Dialogue: … 与卡拉 OK 标签 \\k\\kf\\ko（厘秒）",
    "sourceLineCount": 5,
    "lineCount": 1,
    "tokenCount": 2,
    "timedTokenCount": 2,
    "missingTokenCount": 0,
    "estimatedTokenCount": 0,
    "skippedLineCount": 0,
    "mergedContinuationCount": 0,
    "derivedLineEndCount": 0,
    "fallbackLineEndCount": 0,
    "offsetUs": "0",
    "encodingName": "utf-8",
    "hadByteOrderMark": false,
    "newLineKind": "lineFeed",
    "issues": [
      {
        "kind": "discardedFeature",
        "severity": "info",
        "sourceLineNumber": 0,
        "rawText": "ASS 行内样式与特效标签（\\pos、\\fad、{\\b1} 等）",
        "message": "来源格式里的「ASS 行内样式与特效标签（\\pos、\\fad、{\\b1} 等）」在本项目里没有对应表达，已丢弃。"
      }
    ],
    "detectionReason": "扩展名为 .ass / .ssa，按 ASS 事件行解析",
    "candidateNames": [
      "ASS / SSA 卡拉 OK 字幕（可含逐字）"
    ],
    "isFormatAmbiguous": false,
    "discardedFeatures": [
      "ASS 行内样式与特效标签（\\pos、\\fad、{\\b1} 等）"
    ],
    "splitLineCount": 0,
    "splitCreatedTokenCount": 0,
    "splitEstimatedTokenCount": 0,
    "issueCount": 1,
    "maxSeverity": "info"
  },
  "ustar": {
    "importerId": "ustar",
    "importerName": "UltraStar 卡拉 OK（逐字 + 音高）",
    "sourceFormat": "UltraStar 文本：#BPM / #GAP 头部 + 音符行「: 起始拍 时长 音高 文本」（拍换算成秒）",
    "sourceLineCount": 7,
    "lineCount": 1,
    "tokenCount": 2,
    "timedTokenCount": 2,
    "missingTokenCount": 0,
    "estimatedTokenCount": 0,
    "skippedLineCount": 0,
    "mergedContinuationCount": 0,
    "derivedLineEndCount": 0,
    "fallbackLineEndCount": 0,
    "offsetUs": "0",
    "encodingName": "utf-8",
    "hadByteOrderMark": false,
    "newLineKind": "lineFeed",
    "issues": [
      {
        "kind": "discardedFeature",
        "severity": "info",
        "sourceLineNumber": 0,
        "rawText": "音高（pitch）与打分信息",
        "message": "来源格式里的「音高（pitch）与打分信息」在本项目里没有对应表达，已丢弃。"
      },
      {
        "kind": "formatAmbiguous",
        "severity": "info",
        "sourceLineNumber": 0,
        "rawText": "song.txt",
        "message": "2 个导入器都能处理这个文件，已按优先级选择「UltraStar 卡拉 OK（逐字 + 音高）」：内容含 UltraStar 的 #BPM 头部与音符行"
      }
    ],
    "detectionReason": "内容含 UltraStar 的 #BPM 头部与音符行",
    "candidateNames": [
      "UltraStar 卡拉 OK（逐字 + 音高）",
      "普通 LRC（仅行级时间）"
    ],
    "isFormatAmbiguous": true,
    "discardedFeatures": [
      "音高（pitch）与打分信息"
    ],
    "splitLineCount": 0,
    "splitCreatedTokenCount": 0,
    "splitEstimatedTokenCount": 0,
    "issueCount": 2,
    "maxSeverity": "info"
  },
  "ttml": {
    "importerId": "ttml",
    "importerName": "TTML 逐字歌词（Apple Music / AMLL）",
    "sourceFormat": "TTML 文档：<p begin end> 行 + <span begin end> 音节（itunes:timing=\"Word\"）",
    "sourceLineCount": 1,
    "lineCount": 1,
    "tokenCount": 2,
    "timedTokenCount": 2,
    "missingTokenCount": 0,
    "estimatedTokenCount": 0,
    "skippedLineCount": 0,
    "mergedContinuationCount": 0,
    "derivedLineEndCount": 0,
    "fallbackLineEndCount": 0,
    "offsetUs": "0",
    "encodingName": "utf-8",
    "hadByteOrderMark": false,
    "newLineKind": "none",
    "issues": [],
    "detectionReason": "扩展名为 .ttml，按 TTML 时间文本解析",
    "candidateNames": [
      "TTML 逐字歌词（Apple Music / AMLL）"
    ],
    "isFormatAmbiguous": false,
    "discardedFeatures": [],
    "splitLineCount": 0,
    "splitCreatedTokenCount": 0,
    "splitEstimatedTokenCount": 0,
    "issueCount": 0
  }
}
''')
REPORT_ORACLE = {'scope': 'fixed old/new shared-use-case oracle equality, UTF8/LF/none/no-estimate, mediaDurationUs=6000000; embedded expectations have no runtime artifact dependency', 'oldOracleSha256': '50a8a08f0b2d9b05cb204ca7184fdeab1d910635f66cc5a115e02dec9e864582', 'newOracleSha256': '50a8a08f0b2d9b05cb204ca7184fdeab1d910635f66cc5a115e02dec9e864582', 'oracleProgramSha256': '5a78ee9eb168d613683c8edce757141032edae1e70f4acd927ac41240a9dab75'}


def existing(identifier='primary'):
    return {'kind': 'existing', 'branchId': identifier}


def new_branch(name=' 文件来源 ', role='translation'):
    return {'kind': 'newBranch', 'name': name, 'role': role}


def new_translation(name=' 粘贴翻译 '):
    return {'kind': 'newTranslation', 'name': name}


def file_command(asset, destination=None, **kwargs):
    return {'type': TYPES[0], 'assetId': asset, 'destination': existing() if destination is None else destination, **kwargs}


def append(text=' 新 👩🏽‍🚀 e\u0301 ', destination=None):
    return {'type': TYPES[1], 'text': text, 'destination': existing() if destination is None else destination}


def ttml(rows):
    return '<tt xmlns="http://www.w3.org/ns/ttml"><body><div>' + ''.join(
        f'<p begin="{i * 2}s" end="{i * 2 + 1}s"><span begin="{i * 2}s" end="{i * 2 + 1}s">{text}</span></p>'
        for i, text in enumerate(rows)) + '</div></body></tt>'


class ImportProbe(token.TokenProbe):
    def __init__(self, output, dist):
        super().__init__(output, dist)
        self.inputs = []

    def upload(self, name, data, media='text/plain', *, client='default'):
        if isinstance(data, str):
            data = data.encode('utf-8')
        directory = self.output / 'inputs'
        directory.mkdir(exist_ok=True)
        relative = str(len(self.inputs)) + '-' + name
        (directory / relative).write_bytes(data)
        self.inputs.append({'file': 'inputs/' + relative, 'sha256': hashlib.sha256(data).hexdigest(), 'bytes': len(data),
                            'contentType': media, 'scope': 'probe-owned original input, retained byte-for-byte'})
        save_json(self.output / 'input-manifest.json', self.inputs)
        return self.request('upload-' + str(len(self.inputs)) + '-' + name, 'POST', '/api/v1/assets?fileName=' + quote(name),
            raw=data, client=client, content_type=media, status=201, headers={
                'X-Mako-Request-Id': 'import-upload-' + str(len(self.inputs)),
                'X-Mako-Valid-Until-Utc': self.metadata('upload')['validUntilUtc']})['assetId']

    def request(self, name, method, path, **kwargs):
        result = super().request(name, method, path, **kwargs)
        command = (kwargs.get('value') or {}).get('command', {})
        if command.get('type') in ('history.undo', 'history.redo'):
            self.check('importResult' not in result, name + ': history omits import result')
        return result

    def report(self, name, report, rows):
        self.check(isinstance(report, dict) and set(report) <= REPORT_FIELDS and
            REPORT_FIELDS - {'maxSeverity', 'detectionReason'} <= set(report), name + ': complete original 27-field report contract')
        for key in COUNTS:
            self.check(type(report[key]) is int and report[key] >= 0, name + ': original nonnegative report ' + key)
        tokens = [value for row in rows for value in row['tokens']]
        timed = sum(bool(value['time']) for value in tokens)
        self.check((report['lineCount'], report['tokenCount'], report['timedTokenCount'], report['missingTokenCount'],
                    report['estimatedTokenCount']) == (len(rows), len(tokens), timed, len(tokens) - timed,
                    sum(value['timingStatus'] == 'estimated' for value in tokens)), name + ': all output/token/time counts agree with actual complete rows')
        for key in ('importerId', 'importerName', 'sourceFormat', 'encodingName'):
            self.check(isinstance(report[key], str) and bool(report[key]), name + ': complete report string ' + key)
        self.check(isinstance(report['offsetUs'], str) and str(int(report['offsetUs'])) == report['offsetUs'] and
                   -(2 ** 63) <= int(report['offsetUs']) < 2 ** 63, name + ': exact Int64 offset string')
        self.check(type(report['hadByteOrderMark']) is bool and type(report['isFormatAmbiguous']) is bool and
                   report['newLineKind'] in ('none', 'lineFeed', 'carriageReturnLineFeed', 'carriageReturn', 'mixed'), name + ': original BOM/newline/ambiguity values')
        self.check(all(isinstance(report[key], list) and all(isinstance(value, str) for value in report[key])
                       for key in ('candidateNames', 'discardedFeatures')) and
                   ('detectionReason' not in report or report['detectionReason'] is None or isinstance(report['detectionReason'], str)), name + ': full detection/candidate/discarded values')
        self.check(isinstance(report['issues'], list) and len(report['issues']) == report['issueCount'], name + ': no issue truncation')
        for index, issue in enumerate(report['issues']):
            self.check(set(issue) == {'kind', 'severity', 'sourceLineNumber', 'rawText', 'message'} and issue['kind'] in ISSUES and
                       issue['severity'] in ('info', 'warning', 'error') and type(issue['sourceLineNumber']) is int and
                       issue['sourceLineNumber'] >= 0 and isinstance(issue['rawText'], str) and isinstance(issue['message'], str) and
                       bool(issue['message']), name + ': complete issue fields ' + str(index))
        severities = ('info', 'warning', 'error')
        self.check(report.get('maxSeverity') == (max((issue['severity'] for issue in report['issues']), key=severities.index)
            if report['issues'] else None), name + ': truthful maximum severity')
        if not report['issues']:
            self.check('maxSeverity' not in report, name + ': no fabricated maximum severity for no issues')

    def accepted(self, name, path, command, *, changed=True, undo_name=None, check=None, **unused):
        self.check(changed is True, name + ': imports represent new intents even when input bytes repeat')
        before = self.state(name + '-before', path)
        revision = before['summary']['projectRevision']
        payload = self.payload(name, revision, command)
        result = self.request(name, 'POST', path + '/commands', value=payload)
        after = self.state(name + '-after', path)
        self.check(result['projectSessionId'] == path.rsplit('/', 1)[1] and result['previousRevision'] == revision and
            result['projectRevision'] == str(int(revision) + 1) == after['summary']['projectRevision'] and result['changed'] is True,
            name + ': exact session/revision and one actual commit')
        self.check(all(key not in result for key in ('tokenResult', 'feedback', 'branchResult', 'timingResult', 'failureCode')),
                   name + ': no unrelated success/error fields')
        self.check(result['changeSet'] == {'areas': ['project'], 'branchIds': [], 'lineIds': [], 'tokenIds': [], 'requiresFullRefresh': True},
                   name + ': complete project refresh without partial ID mapping')
        self.check(after['history']['undoCount'] == before['history']['undoCount'] + 1 and after['history']['redoCount'] == 0 and
            result.get('undoName') == undo_name == after['history']['undoName'] and result['isDirty'] and
            after['eventCursor'] != before['eventCursor'], name + ': independent history, dirty and real event')
        value = result.get('importResult')
        is_file = command['type'] == TYPES[0]
        self.check(isinstance(value, dict) and set(value) == ({'lineCount', 'selectedBranchId', 'selectedLineId', 'report'} if is_file else
                   {'lineCount', 'selectedBranchId', 'selectedLineId'}), name + ': exact import result and optional file-only report')
        destination = command['destination']
        identifier = value['selectedBranchId']
        rows = self.rows(after, identifier)
        incoming = rows[-value['lineCount']:] if not is_file else rows
        self.check(type(value['lineCount']) is int and value['lineCount'] > 0 and len(incoming) == value['lineCount'] and
                   value['selectedLineId'] == incoming[0]['id'], name + ': selection points to actual first incoming row')
        source = next(source for source in after['models'][0]['branches'] if source['id'] == identifier)
        if destination['kind'] == 'existing':
            self.check(identifier == destination['branchId'], name + ': exact existing destination identity')
            old_source = next(source for source in before['models'][0]['branches'] if source['id'] == identifier)
            self.check({k: v for k, v in source.items() if k != 'lineCount'} == {k: v for k, v in old_source.items() if k != 'lineCount'},
                       name + ': preserve existing name/role/language/timing strategy')
            if not is_file:
                self.check(rows[:-value['lineCount']] == self.rows(before, identifier), name + ': untouched rows remain byte-value identical')
        else:
            self.check(identifier not in before['models'][2] and source['name'] == destination['name'].strip() and
                source['role'] == (destination.get('role') or 'translation') and source['timingMode'] == ('independent' if is_file else 'followPrimary'),
                name + ': new stable source, original trimmed name, role and explicit timing strategy')
        for source_id, window in before['models'][2].items():
            if source_id != identifier:
                self.check(after['models'][2][source_id]['lines'] == window['lines'], name + ': unrelated source preserved ' + source_id)
        ids = [row['id'] for row in incoming] + [value['id'] for row in incoming for value in row['tokens'] + row['rubyAnnotations']]
        old_ids = {row['id'] for window in before['models'][2].values() for row in window['lines']}
        old_ids.update(value['id'] for window in before['models'][2].values() for row in window['lines'] for value in row['tokens'] + row['rubyAnnotations'])
        self.check(all(isinstance(identifier, str) and 0 < len(identifier) <= 128 for identifier in [value['selectedBranchId'], value['selectedLineId']] + ids)
                   and len(ids) == len(set(ids)) and not old_ids.intersection(ids), name + ': new intent IDs are fresh, unique and within 128')
        if is_file:
            self.report(name, value['report'], incoming)
        else:
            expected = [line for line in command['text'].replace('\r\n', '\n').replace('\r', '\n').split('\n') if line.strip()]
            self.check([row['text'] for row in incoming] == expected and all(row['time'] == {} and row['timingStatus'] == 'missing'
                and all(t['time'] == {} and t['timingStatus'] == 'missing' for t in row['tokens']) for row in incoming),
                name + ': original Unicode/whitespace lines and untimed elements')
        if check:
            check(before, after, value)
        return before, after, result, payload

    def rejected(self, name, path, command, status, code, failure=None, *, field=None, raw_command=None):
        before = self.state(name + '-before', path)
        payload = self.payload(name, before['summary']['projectRevision'], command)
        if raw_command is None:
            result = self.request(name, 'POST', path + '/commands', value=payload, status=status, code=code)
        else:
            prefix = json.dumps({k: v for k, v in payload.items() if k != 'command'})
            save_json(self.output / (name + '-raw-command.json'), {'command': raw_command})
            result = self.request(name, 'POST', path + '/commands', raw=(prefix[:-1] + ',"command":' + raw_command + '}').encode(),
                                  content_type='application/json', status=status, code=code)
        self.check(all(key not in result for key in ('importResult', 'tokenResult', 'feedback', 'branchResult', 'timingResult')),
                   name + ': failure contains no success feedback')
        self.check(result.get('failureCode') == failure if failure else 'failureCode' not in result, name + ': exact optional domain reason')
        if field:
            self.check(result.get('field') == field, name + ': actual command input path')
        self.same_state(before, self.state(name + '-after', path), name)
        return result

    def import_sequence(self, path):
        samples = [
            ('song.lrc', '[00:01.00]你好\n', 'lrc.plain'),
            ('song.lrc', '[00:01.00]<00:01.00>你<00:01.50>好\n', 'lrc.enhanced'),
            ('song.txt', '你好\n', 'text.plain'),
            ('song.srt', '1\n00:00:01,000 --> 00:00:03,000\n你好\n', 'srt'),
            ('song.vtt', 'WEBVTT\n\n00:00:01.000 --> 00:00:03.000\n你好\n', 'vtt'),
            ('song.ass', '[Script Info]\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\\k50}你{\\k50}好\n', 'ass'),
            ('song.txt', '#TITLE:x\n#ARTIST:y\n#BPM:30\n#GAP:1000\n: 0 4 60 你\n: 4 2 62 好\n', 'ustar'),
            ('song.ttml', '<tt xmlns="http://www.w3.org/ns/ttml"><body><div><p begin="00:00:01.000" end="00:00:03.000"><span begin="00:00:01.000" end="00:00:02.000">你</span><span begin="00:00:02.000" end="00:00:03.000">好</span></p></div></body></tt>', 'ttml')]
        reports = {}
        for name, data, importer in samples:
            asset = self.upload(name, data, 'application/octet-stream' if importer in ('srt', 'vtt', 'ass', 'ttml') else 'text/plain')
            self.assets[importer] = asset
            def verify(before, after, value, importer=importer):
                rows = self.rows(after, value['selectedBranchId'])
                self.check(value['report']['importerId'] == importer and [row['text'] for row in rows] == ['你好'], importer + ': detected original format and exact text')
                self.check(''.join(t['text'] for t in rows[0]['tokens']) == '你好', importer + ': full element coverage')
                if importer != 'text.plain':
                    expected_end = '6000000' if importer in ('lrc.plain', 'lrc.enhanced') else '2000000' if importer == 'ass' else ('4000000' if importer == 'ustar' else '3000000')
                    self.check(rows[0]['time'] == {'startUs': '1000000', 'endUs': expected_end} and rows[0]['timingStatus'] == 'imported', importer + ': exact original row range/status')
                if importer in ('lrc.enhanced', 'ass', 'ttml', 'ustar'):
                    expected_times = [{'startUs': '1000000', 'endUs': '1500000'}, {'startUs': '1500000', 'endUs': '2000000'}]
                    if importer == 'lrc.enhanced':
                        expected_times[1]['endUs'] = '6000000'
                    if importer == 'ttml':
                        expected_times = [{'startUs': '1000000', 'endUs': '2000000'}, {'startUs': '2000000', 'endUs': '3000000'}]
                    if importer == 'ustar':
                        expected_times = [{'startUs': '1000000', 'endUs': '3000000'}, {'startUs': '3000000', 'endUs': '4000000'}]
                    self.check([t['time'] for t in rows[0]['tokens']] == expected_times and all(t['timingStatus'] == 'imported' for t in rows[0]['tokens']), importer + ': exact original syllable times')
                else:
                    self.check(all(t['time'] == {} and t['timingStatus'] == 'missing' for t in rows[0]['tokens']), importer + ': no fabricated element times')
                self.check(value['report'] == FORMAT_REPORTS[importer], importer + ': exact entire original 27-field report against fixed independent oracle')
                reports[importer] = copy.deepcopy(value['report'])
            self.exercise('format-' + importer, path, file_command(asset, existing('translation')), undo_name='lyrics.import', check=verify)
        plain = reports['text.plain']
        self.check(plain == FORMAT_REPORTS['text.plain'], 'plain text: complete original report including actual ambiguity diagnostic')
        save_json(self.output / 'eight-format-reports.json', {'oracle': REPORT_ORACLE, 'reports': reports})
        for role in ('translation', 'annotation', 'custom'):
            self.exercise('new-file-' + role, path, file_command(self.assets['srt'], new_branch(' 新文件 ' + role + ' ', role)), undo_name='branch.import')
        self.exercise('append-existing-unicode', path, append(' 保留 👩🏽‍🚀 e\u0301 \r\n\t\r独立行\n\n', existing('translation')), undo_name='lyrics.paste')
        self.exercise('append-new-translation', path, append('译文\n下一行', new_translation()), undo_name='lyrics.paste')
        self.exercise('replace-existing-primary', path, file_command(self.assets['srt']), undo_name='lyrics.import')

    def options_and_reports(self, path):
        asset = self.assets['lrc.enhanced']
        results = []
        for name, options in [('empty', {}), ('null-values', {'importerId': None, 'encodingName': None}),
                              ('unknown-fallback', {'importerId': 'unknown', 'encodingName': 'unknown'})]:
            _, _, result, _ = self.accepted('options-' + name, path, file_command(asset, existing('translation'), options=options), undo_name='lyrics.import')
            results.append(result['importResult']['report'])
        self.check(results[0] == results[1] == results[2], 'default/nullable/unknown format and encoding options retain complete automatic report')
        _, forced, value, _ = self.accepted('options-force-plain', path, file_command(asset, existing('translation'), options={'importerId': 'lrc.plain'}), undo_name='lyrics.import')
        self.check(self.rows(forced, 'translation')[0]['text'] == '<00:01.00>你<00:01.50>好' and
                   value['importResult']['report']['importerId'] == 'lrc.plain', 'explicit importer overrides enhanced automatic choice')
        gb = '[00:00.00]你好'.encode('gb18030')
        for name, data, expected in [('plain', gb, '\ufffd' * 3), ('enhanced', '[00:00.00]<00:00.00>你<00:00.50>好'.encode('gb18030'), '\ufffd' * 4)]:
            asset = self.upload('forced-' + name + '.lrc', data)
            _, state, result, _ = self.accepted('encoding-auto-' + name, path, file_command(asset, existing('translation')), undo_name='lyrics.import')
            self.check(self.rows(state, 'translation')[0]['text'] == '你好' and result['importResult']['report']['encodingName'] == 'gb18030', 'LRC automatic GB18030 decoding preserves Chinese: ' + name)
            _, state, result, _ = self.accepted('encoding-explicit-utf8-' + name, path, file_command(asset, existing('translation'), options={'encodingName': 'utf-8'}), undo_name='lyrics.import')
            report = result['importResult']['report']
            self.check(self.rows(state, 'translation')[0]['text'] == expected and report['encodingName'] == 'utf-8' and
                all(issue['kind'] != 'encodingAssumed' for issue in report['issues']), 'LRC encoding override applies exact replacement decoding: ' + name)
        malformed = '<tt xmlns="http://www.w3.org/ns/ttml"><body><div><p begin="0s" end="1s">有效</p><broken></div></body></tt>'
        asset = self.upload('partial-error.ttml', malformed, 'application/octet-stream')
        _, state, result, payload = self.accepted('report-error-with-valid-lines', path, file_command(asset, existing('translation')), undo_name='lyrics.import')
        report = result['importResult']['report']
        self.check([row['text'] for row in self.rows(state, 'translation')] == ['有效'] and report['maxSeverity'] == 'error' and
            any(issue['kind'] == 'malformedTag' and issue['severity'] == 'error' and issue['sourceLineNumber'] == 0 and issue['rawText'] for issue in report['issues']),
            'complete Error diagnostic with valid rows remains accepted, full rawText/message and legal zero line number')
        self.check(self.request('report-error-full-receipt-retry', 'POST', path + '/commands', value=payload) == result,
                   'full Error issue report replayed exactly with all original diagnostic values')
        self.same_state(state, self.state('report-error-retry-stable', path), 'diagnostic report retry')
        data = b'\xef\xbb\xbf' + ' 留白 e\u0301 👩🏽‍🚀 \r\n\n末行\r'.encode()
        asset = self.upload('unicode-bom.txt', data)
        _, state, result, _ = self.accepted('report-unicode-bom-mixed', path, file_command(asset, existing('translation')), undo_name='lyrics.import')
        self.check([row['text'] for row in self.rows(state, 'translation')] == [' 留白 e\u0301 👩🏽‍🚀 ', '末行'] and
            result['importResult']['report']['hadByteOrderMark'] is True and result['importResult']['report']['newLineKind'] == 'mixed', 'BOM, mixed newline and original Unicode/whitespace retained')
        asset = self.upload('offset-continuation.lrc', '[offset:-123]\n[00:01.00]甲\n续\n[00:07.00]乙\n')
        def rich_report(before, after, value):
            report = value['report']
            rows = self.rows(after, 'translation')
            self.check([row['text'] for row in rows] == ['甲\n续', '乙'] and [row['time'] for row in rows] ==
                [{'startUs': '877000', 'endUs': '6877000'}, {'startUs': '6877000', 'endUs': '10877000'}],
                'offset/continuation: original multiline body and precise derived/fallback microseconds')
            self.check(report['offsetUs'] == '-123000' and report['sourceLineCount'] == 5 and report['mergedContinuationCount'] == 1 and
                report['derivedLineEndCount'] == 1 and report['fallbackLineEndCount'] == 1 and
                any(issue['kind'] == 'lineEndFallback' and issue['sourceLineNumber'] == 4 and issue['rawText'] == '乙' for issue in report['issues']),
                'report: nonzero original offset/continuation/derived/fallback counters with complete source issue')
        self.exercise('rich-offset-continuation-report', path, file_command(asset, existing('translation')), undo_name='lyrics.import', check=rich_report)
        for rule in ('none', 'mixed', 'perTextElement', 'latinWords', 'byPunctuation', 'byWhitespace'):
            self.exercise('split-option-' + rule, path, file_command(self.assets['srt'], existing('translation'),
                options={'splitRule': rule, 'estimateSplitTimes': True}), undo_name='lyrics.import')

    def malformed(self, path):
        good = file_command(self.assets['text.plain'])
        cases = [('missing-asset', {k: v for k, v in good.items() if k != 'assetId'}), ('null-asset', {**good, 'assetId': None}),
                 ('numeric-asset', {**good, 'assetId': 1}), ('missing-destination', {'type': TYPES[0], 'assetId': good['assetId']}),
                 ('null-destination', {**good, 'destination': None}), ('array-destination', {**good, 'destination': []}),
                 ('import-wrong-destination', file_command(good['assetId'], new_translation())),
                 ('append-wrong-destination', append('有效', new_branch())), ('missing-text', {'type': TYPES[1], 'destination': existing()}),
                 ('null-text', append(None)), ('number-text', append(1)), ('append-options', {**append(), 'options': {}}),
                 ('options-null', {**good, 'options': None}), ('options-array', {**good, 'options': []}),
                 ('options-unknown', {**good, 'options': {'extra': None}})]
        for destination in ({}, {'kind': None}, {'kind': 1}, {'kind': 'Existing', 'branchId': 'primary'},
            {'kind': 'existing'}, {'kind': 'existing', 'branchId': None}, {'kind': 'existing', 'branchId': 1},
            {**existing(), 'name': None}, {**existing(), 'role': None}, {**existing(), 'extra': None},
            {'kind': 'newBranch', 'name': 'x'}, {'kind': 'newBranch', 'role': 'custom'},
            {'kind': 'newBranch', 'name': None, 'role': 'custom'}, {**new_branch(), 'branchId': None},
            *[new_branch(role=value) for value in ('primary', 'Primary', '0', 'unknown', 0, None)]):
            cases.append(('destination-' + str(len(cases)), {**good, 'destination': destination}))
        for key, values in {'importerId': [1, False, []], 'encodingName': [1, False, []],
            'splitRule': [None, 0, '0', 'None', 'unknown'], 'estimateSplitTimes': [None, 0, 'false', []]}.items():
            for value in values:
                cases.append(('options-' + key + '-' + str(len(cases)), {**good, 'options': {key: value}}))
        for command in (good, append()):
            for key in ('target', 'startUs', 'endUs', 'analysisJobId', 'branchId', 'text' if command['type'] == TYPES[0] else 'assetId', 'extra'):
                cases.append(('cross-field-' + str(len(cases)), {**command, key: None}))
        for name, command in cases:
            self.rejected('shape-' + name, path, command, 400, 'invalidRequest')
        for index, destination in enumerate(({'kind': 'newTranslation'}, {'kind': 'newTranslation', 'name': None},
            {'kind': 'newTranslation', 'name': 1}, {**new_translation(), 'role': None}, {**new_translation(), 'branchId': None})):
            self.rejected('shape-new-translation-' + str(index), path, append('有效', destination), 400, 'invalidRequest')
        for name, raw in [('type', json.dumps(good)[:-1] + ',"type":"lyrics.importAsset"}'),
            ('asset', json.dumps(good)[:-1] + ',"assetId":"x"}'),
            ('destination', '{"type":"lyrics.appendText","text":"a","destination":{"kind":"existing","branchId":"primary","branchId":"primary"}}'),
            ('options', '{"type":"lyrics.importAsset","assetId":' + json.dumps(good['assetId']) + ',"destination":{"kind":"existing","branchId":"primary"},"options":{"splitRule":"none","splitRule":"none"}}')]:
            self.rejected('shape-duplicate-' + name, path, good, 400, 'invalidRequest', raw_command=raw)
        self.rejected('empty-append', path, append(' \r\n\t\r '), 422, 'emptyLyrics', 'EmptyText', field='command.text')
        asset = self.upload('empty.txt', ' \r\n\t')
        self.rejected('empty-import', path, file_command(asset), 422, 'emptyLyrics', 'EmptyImport', field='command')
        self.rejected('missing-target', path, append('有效', existing('missing')), 422, 'commandRejected', 'TargetStale', field='command.destination.branchId')
        for command in (file_command(good['assetId'], new_branch(' ')), append('有效', new_translation(' '))):
            self.rejected('blank-new-name-' + command['type'].split('.')[-1], path, command, 400, 'invalidRequest', field='command.destination.name')
        self.rejected('empty-asset', path, file_command(' '), 400, 'invalidRequest', field='command.assetId')
        self.rejected('invalid-asset', path, file_command('missing'), 404, 'resourceNotFound', field='command.assetId')
        self.rejected('long-destination', path, append('新', existing('x' * 129)), 400, 'invalidRequest', field='command.destination.branchId')

    def receipts_and_races(self, path):
        commands = [file_command(self.assets['srt'], new_branch(' 回执文件 ', 'custom'), options={}),
                    append('回执文本', new_translation(' 回执翻译 '))]
        for index, command in enumerate(commands):
            name = 'receipt-' + str(index)
            _, after, original, payload = self.accepted(name, path, command, undo_name='branch.import' if index == 0 else 'lyrics.paste')
            retry = {**payload, 'expectedRevision': '000' + payload['expectedRevision']}
            if index == 0:
                retry['command'] = {**command, 'options': {'importerId': None, 'encodingName': None, 'splitRule': 'none', 'estimateSplitTimes': False}}
            self.check(self.request(name + '-canonical-stale-retry', 'POST', path + '/commands', value=retry) == original,
                       name + ': original full report/selection/IDs returned without regenerating on retry')
            variants = []
            if index == 0:
                variants += [{**command, 'assetId': self.assets['vtt']}, {k: v for k, v in command.items() if k != 'options'},
                    {**command, 'options': {'splitRule': 'none', 'estimateSplitTimes': True}},
                    {**command, 'options': {'importerId': 'srt'}}, {**command, 'options': {'encodingName': 'utf-8'}},
                    {**command, 'options': {'importerId': 'unknown'}}, {**command, 'options': {'encodingName': 'unknown'}},
                    {**command, 'options': {'splitRule': 'mixed'}},
                    {**command, 'destination': {**command['destination'], 'role': 'annotation'}}]
            else:
                variants += [{**command, 'text': command['text'] + '改'}]
            variants += [{**command, 'destination': {**command['destination'], 'name': '另名'}}, {**command, 'destination': existing()}]
            for number, different in enumerate(variants):
                self.request(name + '-identity-reuse-' + str(number), 'POST', path + '/commands', value={**payload, 'command': different}, status=409, code='requestIdReuse')
            self.request(name + '-revision-reuse', 'POST', path + '/commands', value={**payload, 'expectedRevision': after['summary']['projectRevision']}, status=409, code='requestIdReuse')
            deadline = content.timing.s1.smoke.existing.iso(content.timing.s1.smoke.existing.utc_now() + content.timing.s1.smoke.existing.timedelta(seconds=95))
            self.request(name + '-deadline-reuse', 'POST', path + '/commands', value={**payload, 'validUntilUtc': deadline}, status=409, code='requestIdReuse')
            self.same_state(after, self.state(name + '-stable', path), name + ': retries/reuses do not mutate')
            self.history(name + '-undo', path, 'undo')
            self.check(self.request(name + '-retry-after-undo', 'POST', path + '/commands', value=payload) == original,
                       name + ': undo never invalidates historical receipt or regenerates IDs')
            self.same_content(after, self.history(name + '-redo', path, 'redo'), name + ': full independent redo')
            _, _, repeated, _ = self.accepted(name + '-same-command-new-intent', path, copy.deepcopy(command),
                undo_name='branch.import' if index == 0 else 'lyrics.paste')
            self.check(repeated['importResult']['selectedBranchId'] != original['importResult']['selectedBranchId'] and
                repeated['importResult']['selectedLineId'] != original['importResult']['selectedLineId'],
                name + ': identical command with a new request really creates new IDs/history')
        before = self.state('distinct-race-before', path)
        revision = before['summary']['projectRevision']
        payloads = [self.payload('distinct-race-' + str(i), revision, append('并发' + str(i), existing('translation'))) for i in range(2)]
        results = self.concurrent('import-distinct-race', path, payloads)
        self.check(sum(result[0] == 200 for result in results) == 1 and all(result[0] in (200, 409, 429) for result in results),
                   'same-revision distinct intents: one commit, loser may hit unqueued preparation slot or exact revision')
        for status, _, result, _ in results:
            if status != 200:
                self.check((status, result['code'], result.get('field')) in ((409, 'revisionConflict', 'expectedRevision'), (429, 'requestQuotaExceeded', 'command')),
                           'race: precise revision/slot rejection, no assertion of internal checkpoint timing')
        after = self.state('distinct-race-after', path)
        self.check(after['history']['undoCount'] == before['history']['undoCount'] + 1 and after['summary']['projectRevision'] == str(int(revision) + 1), 'distinct race: one history and revision')
        payload = self.payload('same-owner', after['summary']['projectRevision'], file_command(self.assets['srt'], new_branch('同回执')))
        results = self.concurrent('import-same-receipt-race', path, [payload, copy.deepcopy(payload)])
        self.check([result[0] for result in results] == [200, 200] and results[0][2] == results[1][2], 'same receipt concurrent: identical entire report/IDs and one owner')
        latest = self.state('same-owner-after', path)
        self.check(latest['summary']['projectRevision'] == str(int(revision) + 2) and latest['history']['undoCount'] == after['history']['undoCount'] + 1,
                   'same owner: one allocation outcome/commit/history, not two new sources')
        self.request('exact-stale-new-intent', 'POST', path + '/commands', value=self.payload('stale', revision, append()), status=409, code='revisionConflict')
        self.same_state(latest, self.state('stale-after', path), 'stale new intent')

    def auth(self, path):
        before = self.state('auth-before', path)
        other = self.register('import-other', select=False)
        self.secrets.extend((other['accessToken'], other['resumeToken']))
        for command in (file_command(self.assets['srt']), append()):
            self.request('cross-client-session-' + command['type'].split('.')[-1], 'POST', path + '/commands', client=other['clientId'],
                value=self.payload('cross-client-session', before['summary']['projectRevision'], command), status=404, code='sessionNotFound')
        asset = self.upload('foreign.txt', '其他客户端', client=other['clientId'])
        self.rejected('foreign-asset', path, file_command(asset), 401, 'resourceUnauthorized', field='command.assetId')
        self.rejected('audio-asset-not-lyrics', path, file_command(self.audio_asset), 415, 'unsupportedMediaType', field='command.assetId')
        self.same_state(before, self.state('auth-after', path), 'session and resource authorization')
        asset = self.upload('integrity.txt', '原')
        candidates = list((self.output / 'profile/runs').glob('*/resources/' + self.client + '/' + asset + '.txt'))
        self.check(len(candidates) == 1, 'exact probe-owned resource copy found without reading bootstrap')
        resource = candidates[0]
        original = resource.read_bytes()
        self.check(original == '原'.encode(), 'uploaded resource starts with exact original bytes')
        try:
            resource.write_bytes('坏'.encode())
            self.rejected('fixed-resource-sha-mismatch', path, file_command(asset), 422, 'lyricsImportFailed', field='command.assetId')
        finally:
            resource.write_bytes(original)
        self.exercise('fixed-resource-restored-new-intent', path, file_command(asset, existing('translation')), undo_name='lyrics.import')
        save_json(self.output / 'resource-integrity-fixture.json', {'assetId': asset, 'originalSha256': hashlib.sha256(original).hexdigest(),
            'mutatedSha256': hashlib.sha256('坏'.encode()).hexdigest(), 'restored': resource.read_bytes() == original,
            'scope': 'only probe-owned uploaded storage copy changed temporarily; retained original input never changed'})
        relative = self.fixture('import-readonly', self.fixture_content)
        handle = self.request('readonly-handle', 'POST', '/api/v1/file-handles', status=201, value={'rootGrantId': 'projects', 'relativePath': relative})
        opened = self.job('readonly-open', self.request('readonly-open', 'POST', '/api/v1/project-sessions/open-jobs', status=202,
            value={'projectDirectoryHandleId': handle['handleId'], 'access': 'read'}))['result']
        readonly_path = '/api/v1/project-sessions/' + opened['projectSessionId']
        self.check(opened['isReadOnly'], 'actual readonly session')
        for command in (file_command(self.assets['srt']), append()):
            self.rejected('readonly-' + command['type'].split('.')[-1], readonly_path, command, 403, 'readOnlySession', field='command')
        self.close('readonly-close', readonly_path, opened['projectRevision'])

    def budgets(self, path):
        # Establish a real redo before each resource rejection. No parsing or
        # response budget failure may consume it or publish an event.
        self.accepted('budget-setup', path, append('预算前置'), undo_name='lyrics.paste')
        self.history('budget-setup-undo', path, 'undo')
        cases = [
            ('logical-64mib', 'logical.txt', 'a' * 150000, {'importerId': 'text.plain'}),
            ('complete-report', 'report.lrc', '\n'.join('[unknown' + str(i) + ']' for i in range(1000)) + '\n[00:01.00]有效', {}),
            ('single-line-bytes', 'large.ttml', ttml(['界' * 90000]), {}),
            ('cumulative-16mib', 'cumulative.ttml', ttml(['界' * 80000] * 18), {}),
            ('single-line-items', 'items.txt', '字' * 20000, {'importerId': 'text.plain'})]
        for name, filename, data, options in cases:
            asset = self.upload(filename, data, 'application/octet-stream')
            before = self.state(name + '-stable-before', path)
            command = file_command(asset, existing('translation'), options=options)
            payload = self.payload(name, before['summary']['projectRevision'], command)
            result = self.request(name, 'POST', path + '/commands', value=payload, status=413, code='resourceTooLarge')
            self.check(result.get('field') == 'command' and 'failureCode' not in result and 'importResult' not in result, name + ': exact bounded failure without truncated report/result')
            self.check(self.request(name + '-same-receipt-retry', 'POST', path + '/commands', value=payload, status=413, code='resourceTooLarge') == result,
                       name + ': complete stable failure receipt')
            self.same_state(before, self.state(name + '-stable-after', path), name + ': whole content/history/redo/dirty/revision/event unchanged')
        self.rejected('append-single-line-items', path, append('字' * 20000), 413, 'resourceTooLarge', field='command')
        self.check(17 * 80000 * 12 < 16 * 1024 * 1024 < 18 * 80000 * 12 and 80000 * 12 < 1024 * 1024,
                   'cumulative fixture: repeated CJK row+token JSON text lower bound crosses 16MiB while each row stays below 1MiB')
        asset = self.upload('cumulative-control.ttml', ttml(['界' * 80000] * 17), 'application/octet-stream')
        self.exercise('cumulative-below-16mib-control', path, file_command(asset, existing('translation')), undo_name='lyrics.import',
            check=lambda before, after, value: self.check(len(self.rows(after, 'translation')) == 17 and
                all(len(row['tokens']) == 1 and row['text'] == '界' * 80000 for row in self.rows(after, 'translation')),
                '17 low-token rows genuinely commit and are fully readable at the same exact revision'))
        # Actual low-token line controls keep parser work small and prove a long
        # merged line is not rejected merely for its grapheme count.
        asset = self.upload('long-merged-control.ttml', ttml(['界' * 80000]), 'application/octet-stream')
        self.exercise('legal-long-merged-control', path, file_command(asset, existing('translation')), undo_name='lyrics.import',
            check=lambda before, after, value: self.check(len(self.rows(after, 'translation')[0]['tokens']) == 1 and
                self.rows(after, 'translation')[0]['text'] == '界' * 80000, 'actual complete long merged token is legal below byte budget'))
        self.accepted('budget-recovery-valid-command', path, append('预算后可继续', existing('translation')), undo_name='lyrics.paste')

    def identifier_fixtures(self):
        def fixture(project, lyrics):
            self.fixture_content(project, lyrics)
            lyrics['branches'][1]['lines'][0]['id'] = 'l' * 129
            lyrics['branches'][1]['lines'][0]['tokens'][0]['id'] = 't' * 129
            lyrics['branches'][2]['id'] = 'b' * 128
            lyrics['branches'].append({'id': 'b' * 129, 'name': '旧长来源', 'role': 'Custom', 'timingMode': 'None', 'lines': []})
        relative = self.fixture('import-old-long-ids', fixture)
        path, opened = self.open('long-ids-open', relative)
        self.exercise('append-old-untouched-id129', path, append('新行', existing('translation')), undo_name='lyrics.paste')
        self.exercise('append-destination-id128', path, append('边界', existing('b' * 128)), undo_name='lyrics.paste')
        self.rejected('append-existing-branch129', path, append('拒绝', existing('b' * 129)), 400, 'invalidRequest', field='command.destination.branchId')
        self.rejected('replace-existing-branch129', path, file_command(self.assets['srt'], existing('b' * 129)), 400, 'invalidRequest', field='command.destination.branchId')
        self.exercise('replace-remaps-old-line129', path, file_command(self.assets['srt'], existing('translation')), undo_name='lyrics.import')
        self.close_roundtrip('import-long-ids', path)

    def normal_fixture(self, project, lyrics):
        self.fixture_content(project, lyrics)
        lyrics['branches'][1]['styleOverride'] = {'typography': {'fontSizePx': 27}, 'opacity': .75}

    def finish(self):
        self.check(bool(self.worker_identities), 'actual Worker preparation identities observed')
        self.stop('import')
        for value in self.inputs:
            data = (self.output / value['file']).read_bytes()
            self.check(len(data) == value['bytes'] and hashlib.sha256(data).hexdigest() == value['sha256'], 'original uploaded input bytes unchanged: ' + value['file'])
        self.check(len(self.samples) == self.request_count, 'every HTTP call has a local short elapsed sample')
        save_json(self.output / 'http-elapsed.json', {'samples': self.samples,
            'scope': 'single local short samples including client validation/log overhead; no allocation/performance/throughput/platform guarantee'})
        scanned = 0
        for file in self.output.rglob('*'):
            if file.is_file():
                self.check(not any(secret.encode() in file.read_bytes() for secret in self.secrets), 'credential scan: ' + str(file.relative_to(self.output)))
                scanned += 1
        label = 'credential scan: summary.json'
        candidate = self.output / '.summary.candidate'
        save_json(candidate, {'status': 'passed', 'httpRequests': self.request_count, 'assertions': len(self.assertions) + 1,
            'assertionLabels': self.assertions + [label], 'credentialScannedFiles': scanned + 1, 'runs': self.runs, 'fixtures': self.fixtures,
            'inputManifest': self.inputs, 'workerIdentities': [{'pid': p, 'startTicks': t} for p, t in sorted(self.worker_identities)],
            'scope': 'S3d two actual HTTP import/append commands and all eight original import formats; owned fixtures, full reports, budgets/history/persistence; HTTP cannot establish internal preparation checkpoint timing, slot exhaustion or shutdown-owner drain; excludes browser/render/performance/platform acceptance'})
        self.check(not any(secret.encode() in candidate.read_bytes() for secret in self.secrets), label)
        candidate.rename(self.output / 'summary.json')
        print(json.dumps({'status': 'passed', 'httpRequests': self.request_count, 'assertions': len(self.assertions)}))

    def run(self):
        self.start('import')
        self.connect('import')
        self.assets = {}
        capabilities = self.request('import-capabilities', 'GET', '/api/v1/capabilities')
        for command in TYPES:
            self.check('project-sessions.commands.' + command in capabilities['features'], 'advertised S3d command ' + command)
        buf = io.BytesIO()
        with wave.open(buf, 'wb') as wav:
            wav.setnchannels(1); wav.setsampwidth(2); wav.setframerate(8000); wav.writeframes(b'\x00\x00' * 48000)
        self.audio_asset = self.upload('import.wav', buf.getvalue(), 'audio/wav')
        asset = self.upload('template.lrc', '[00:00.00]甲乙\n')
        created = self.request('create-template', 'POST', '/api/v1/project-sessions', status=201,
            value={'name': 'S3d import HTTP probe', 'audioAssetId': self.audio_asset, 'lyricsAssetId': asset})
        path = '/api/v1/project-sessions/' + created['projectSessionId']
        path, opened = self.save_close_open('import-template', path, created['projectRevision'], 'editing-original')
        self.close('import-template-close', path, opened['projectRevision'])
        relative = self.fixture('import-normal', self.normal_fixture)
        path, opened = self.open('import-normal-open', relative)
        self.import_sequence(path)
        self.options_and_reports(path)
        self.malformed(path)
        self.receipts_and_races(path)
        self.auth(path)
        self.budgets(path)
        self.close_roundtrip('import-normal', path)
        saved = self.output / 'profile/projects/import-normal-saved'
        project = json.loads((saved / 'project.json').read_text())
        lyrics = json.loads((saved / project['lyricsFile']).read_text())
        source = next(value for value in lyrics['branches'] if value['id'] == 'translation')
        self.check(source.get('styleOverride') == {'typography': {'fontSizePx': 27}, 'opacity': .75},
                   'persisted existing source retains original complete style override through replace/append/save/reopen')
        self.identifier_fixtures()
        self.finish()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    args.output = args.output.resolve()
    if not args.output.is_relative_to(content.timing.s1.REPO / 'artifacts'):
        parser.error('--output must be a new directory beneath repository artifacts/')
    args.output.mkdir(parents=True, exist_ok=False)
    probe = ImportProbe(args.output, args.dist.resolve())
    try:
        probe.run()
    finally:
        if probe.launcher is not None:
            probe.stop('failure-cleanup')


if __name__ == '__main__':
    main()
