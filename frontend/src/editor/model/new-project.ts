// 新建工程对话框（W5 设计 §1、§2.6）：四种组合、默认名称。歌词导入规划的识别结果与改选候选在 lyrics-import.ts。纯函数，不依赖 React。

import { fileStem } from './lyrics-import';

/** 音频文件的选择范围（新建工程、空态、设置共用）。 */
export const AUDIO_FILE_ACCEPT = 'audio/*,.wav,.mp3,.flac,.m4a,.ogg,.aac';

export type NewProjectKind = 'full' | 'lyricsOnly' | 'audioOnly' | 'empty';

export function newProjectKind(hasAudio: boolean, hasLyrics: boolean): NewProjectKind {
  if (hasAudio) return hasLyrics ? 'full' : 'audioOnly';
  return hasLyrics ? 'lyricsOnly' : 'empty';
}

/** 第二步顶部的说明：这次会建什么样的工程、之后去哪里补。 */
export const KIND_TEXT: Record<NewProjectKind, string> = {
  full: '用所选的音频和歌词新建工程。',
  lyricsOnly: '只有歌词：没有时间轴，之后可以在设置里选择音频。',
  audioOnly: '只有音频：之后可以在歌词页导入歌词文件。',
  empty: '会创建一个空工程，之后可以在歌词页导入歌词、在设置里选择音频。',
};

/** 名称不填时依次用音频文件名、歌词文件名、“新工程”。 */
export function defaultProjectName(audioFileName?: string, lyricsFileName?: string): string {
  return (audioFileName && fileStem(audioFileName)) || (lyricsFileName && fileStem(lyricsFileName)) || '新工程';
}
