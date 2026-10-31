// 在浏览器里生成演示素材：2 秒正弦音 WAV 与两行 LRC。远小于 Server 默认 16 MiB 资源上限。

export function makeSampleWav(seconds = 4, sampleRate = 22050, frequency = 440): File {
  const samples = Math.round(seconds * sampleRate);
  const buffer = new ArrayBuffer(44 + samples * 2);
  const view = new DataView(buffer);
  const ascii = (offset: number, text: string) => [...text].forEach((c, i) => view.setUint8(offset + i, c.charCodeAt(0)));
  ascii(0, 'RIFF');
  view.setUint32(4, 36 + samples * 2, true);
  ascii(8, 'WAVE');
  ascii(12, 'fmt ');
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  ascii(36, 'data');
  view.setUint32(40, samples * 2, true);
  for (let i = 0; i < samples; i++) {
    const envelope = Math.min(1, i / 2000, (samples - i) / 2000);
    view.setInt16(44 + i * 2, Math.round(Math.sin((2 * Math.PI * frequency * i) / sampleRate) * 8000 * envelope), true);
  }
  return new File([buffer], '示例音频.wav', { type: 'audio/wav' });
}

export function makeSampleLrc(): File {
  const text = '[00:00.50]你好，协议控制台\n[00:02.00]こんにちは、カラオケ\n[00:03.00]Third line\n';
  return new File([text], '示例歌词.lrc', { type: 'text/plain' });
}
