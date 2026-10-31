// 注音片段（W6 设计 §2.1、§2.4、§2.5、§2.6）：查找、整段文字能否直接改、左右移的新顺序、按字拆开与删除注音的命令序列、
// 时间栏里的 ±1 帧与取播放头、时间轴注音轨的块。纯函数，不依赖 React。

import type { ProjectEditCommand, RubyAnnotation, RubyGroupTarget, RubySegmentTarget, RubyTextSegment, TimingStatus } from '../../protocol';
import { isTimed, type EditorLine, type Selection } from './lyrics';
import { frameDurationUs, fromLyricTime, snapToFrame, type FrameRate } from './time';
import { graphemes } from './token-segments';

/** 文本单元的注音组；一个单元最多一组。 */
export function rubyOf(line: EditorLine, tokenId: string): RubyAnnotation | undefined {
  return line.ruby.find((r) => r.baseTokenId === tokenId);
}

export function groupTarget(line: EditorLine, ruby: RubyAnnotation): RubyGroupTarget {
  return { branchId: line.branchId, lineId: line.id, tokenId: ruby.baseTokenId, rubyId: ruby.id };
}

export function segmentTarget(line: EditorLine, ruby: RubyAnnotation, segmentId: string): RubySegmentTarget {
  return { ...groupTarget(line, ruby), segmentId };
}

export function segmentTimed(segment: RubyTextSegment): boolean {
  return segment.time.startUs !== undefined && segment.time.endUs !== undefined;
}

/** 所选片段：属于所选文本单元的注音组时才有效。 */
export function selectedSegment(line: EditorLine, selection: Selection | undefined): { ruby: RubyAnnotation; segment: RubyTextSegment; index: number } | undefined {
  if (!selection?.tokenId || !selection.segmentId || selection.lineId !== line.id) return undefined;
  const ruby = rubyOf(line, selection.tokenId);
  const index = ruby ? ruby.segments.findIndex((s) => s.id === selection.segmentId) : -1;
  return ruby && index >= 0 ? { ruby, segment: ruby.segments[index]!, index } : undefined;
}

/**
 * 整段文字不能直接改的原因：注音有多个片段或片段已有时间时，后端拒绝 setRubyText（W6 设计 §0）。
 * 没有注音、或只有一个没有时间的片段时返回 undefined。
 */
export function rubyTextLocked(ruby: RubyAnnotation | undefined): string | undefined {
  if (!ruby) return undefined;
  if (ruby.segments.length > 1) return '注音已分成几个片段，请逐个片段修改';
  if (ruby.segments.some(segmentTimed)) return '注音片段已有时间，请在片段里修改';
  return undefined;
}

/** 片段左移（-1）或右移（+1）后的完整顺序；到头时为 undefined。 */
export function movedOrder(ruby: RubyAnnotation, segmentId: string, delta: -1 | 1): string[] | undefined {
  const ids = ruby.segments.map((s) => s.id);
  const from = ids.indexOf(segmentId);
  const to = from + delta;
  if (from < 0 || to < 0 || to >= ids.length) return undefined;
  [ids[from], ids[to]] = [ids[to]!, ids[from]!];
  return ids;
}

/** 能按字拆开的片段：没有时间、至少两个字。 */
function splittable(segment: RubyTextSegment): boolean {
  return !segmentTimed(segment) && graphemes(segment.text).length >= 2;
}

/** 按字拆开不能做的原因；能做时为 undefined。 */
export function splitBlocked(ruby: RubyAnnotation): string | undefined {
  if (ruby.segments.some(splittable)) return undefined;
  if (ruby.segments.some((s) => graphemes(s.text).length >= 2)) return '有时间的片段不拆：拆开后时间怎么分没有依据';
  return '每个片段都只有一个字';
}

/**
 * 按字拆开（一次 applyEdits）：每个可拆的片段，先把第 2 个字起倒序加在它后面（afterSegmentId 都是它），
 * 最后把它自己改成第一个字。顺序正好；最后一条是改字，结果建议选中原片段。
 */
export function splitEdits(line: EditorLine, ruby: RubyAnnotation): ProjectEditCommand[] {
  const group = groupTarget(line, ruby);
  const edits: ProjectEditCommand[] = [];
  for (const segment of ruby.segments) {
    if (!splittable(segment)) continue;
    const [first, ...rest] = graphemes(segment.text);
    for (const text of rest.reverse()) edits.push({ type: 'lyrics.addRubySegment', target: group, text, afterSegmentId: segment.id });
    edits.push({ type: 'lyrics.setRubySegmentText', target: { ...group, segmentId: segment.id }, text: first! });
  }
  return edits;
}

/** 删除注音（一次 applyEdits）：逐个删除片段，删除最后一个时整组注音一起删除。 */
export function removeRubyEdits(line: EditorLine, ruby: RubyAnnotation): ProjectEditCommand[] {
  return ruby.segments.map((s) => ({ type: 'lyrics.removeRubySegment', target: segmentTarget(line, ruby, s.id) }));
}

// ── 片段时间（W6 设计 §2.5）：后端没有片段的 nudge 与打点命令，在本地算好起止再发 setRubySegmentTime ──

export interface TimeRange {
  startUs: number;
  endUs: number;
}

/** 帧率未知时按 30 fps 算一帧，也不吸附。 */
const FALLBACK_FRAME_US = 1_000_000 / 30;

const snap = (us: number, rate: FrameRate | undefined) => (rate ? snapToFrame(us, rate) : Math.round(us));
const frameUs = (rate: FrameRate | undefined) => (rate ? frameDurationUs(rate) : FALLBACK_FRAME_US);

/** 起止一起移 frames 帧并吸附；移到 0 之前时为 undefined。 */
export function nudgedRange(range: TimeRange, frames: number, rate: FrameRate | undefined): TimeRange | undefined {
  const delta = frames * frameUs(rate);
  const startUs = snap(range.startUs + delta, rate);
  const endUs = snap(range.endUs + delta, rate);
  return startUs < 0 || endUs <= startUs ? undefined : { startUs, endUs };
}

/** 起点取播放头：原终点晚于新起点时保留，否则终点设为起点后一帧。 */
export function stampedStart(range: { startUs?: number; endUs?: number }, playheadUs: number, rate: FrameRate | undefined): TimeRange {
  const startUs = snap(playheadUs, rate);
  const endUs = range.endUs !== undefined && range.endUs > startUs ? range.endUs : snap(startUs + frameUs(rate), rate);
  return { startUs, endUs };
}

/** 终点取播放头：需要已有早于播放头的起点；不能做时返回原因。 */
export function stampedEnd(range: { startUs?: number; endUs?: number }, playheadUs: number, rate: FrameRate | undefined): TimeRange | string {
  if (range.startUs === undefined) return '先设起点';
  const endUs = snap(playheadUs, rate);
  if (endUs <= range.startUs) return '播放头要晚于起点';
  return { startUs: range.startUs, endUs };
}

// ── 时间轴注音轨（W6 设计 §2.6）──

/** 注音轨上的一块：有时间的片段；或整组都没有时间时，在文本单元的位置画的虚线框（follow）。 */
export interface RubyBand {
  /** 片段 ID；虚线框是 `follow:注音组 ID`。 */
  id: string;
  lineId: string;
  tokenId: string;
  rubyId: string;
  /** 点选时选中的片段：虚线框选第一个片段。 */
  segmentId: string;
  text: string;
  startUs: number;
  endUs: number;
  status: TimingStatus;
  follow: boolean;
}

export function hasRuby(lines: readonly EditorLine[]): boolean {
  return lines.some((line) => line.ruby.length > 0);
}

/** 注音轨要画的块，按行、注音组、片段的顺序。部分片段有时间时只画有时间的片段；整组没有时间且文本单元也没有时间时不画。 */
export function rubyBands(lines: readonly EditorLine[]): RubyBand[] {
  const bands: RubyBand[] = [];
  for (const line of lines) {
    for (const ruby of line.ruby) {
      const base = { lineId: line.id, tokenId: ruby.baseTokenId, rubyId: ruby.id };
      const timed = ruby.segments.filter(segmentTimed);
      if (timed.length > 0) {
        for (const s of timed) {
          const time = fromLyricTime(s.time);
          if (isTimed(time)) bands.push({ ...base, id: s.id, segmentId: s.id, text: s.text, startUs: time.startUs, endUs: time.endUs, status: s.timingStatus, follow: false });
        }
        continue;
      }
      const token = line.tokens.find((t) => t.id === ruby.baseTokenId);
      const first = ruby.segments[0];
      if (!token || !first || !isTimed(token)) continue;
      bands.push({ ...base, id: `follow:${ruby.id}`, segmentId: first.id, text: ruby.text, startUs: token.startUs, endUs: token.endUs, status: first.timingStatus, follow: true });
    }
  }
  return bands;
}

/** 注音块是否选中：片段按片段 ID；虚线框只要选中了它的文本单元就算。 */
export function rubyBandSelected(band: RubyBand, selection: Selection | undefined): boolean {
  if (!selection || selection.lineId !== band.lineId || selection.tokenId !== band.tokenId) return false;
  return band.follow || selection.segmentId === band.segmentId;
}
