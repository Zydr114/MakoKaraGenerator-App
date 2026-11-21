// 专用 Web Worker：懒加载 @patdx/kuromoji（Apache-2.0），通过自定义 LoaderConfig
// 从 OPFS / IndexedDB 持久缓存读取用户按需下载或导入的 NAIST-jdic 0.6.3b（BSD-3-Clause）。
// 绝不引用 @patdx/kuromoji/dict（IPADIC），主线程 60fps 零卡顿。

import { TokenizerBuilder, type IpadicFeatures } from '@patdx/kuromoji';
import { gunzipBytes, JapaneseDictStore } from './dict-store';

export interface FuriganaWorkerRequest {
  id: number;
  type: 'init' | 'tokenize' | 'reset';
  texts?: string[];
}

export interface FuriganaWorkerMorpheme {
  surface_form: string;
  reading?: string;
  pos?: string;
}

export interface FuriganaWorkerResponse {
  id: number;
  ok: boolean;
  tokensList?: FuriganaWorkerMorpheme[][];
  error?: string;
}

type KuromojiTokenizer = {
  tokenize(text: string): IpadicFeatures[];
};

const store = new JapaneseDictStore();
let tokenizerPromise: Promise<KuromojiTokenizer> | undefined;

async function getTokenizer(): Promise<KuromojiTokenizer> {
  if (!tokenizerPromise) {
    tokenizerPromise = (async () => {
      const status = await store.getStatus();
      if (!status.installed) {
        throw new Error('日语词典尚未下载或导入，请先在词典管理中下载 NAIST-jdic 0.6.3b。');
      }
      const builder = new TokenizerBuilder({
        loader: {
          async loadArrayBuffer(filename: string): Promise<ArrayBufferLike> {
            const cleanName = filename.split('/').pop() ?? filename;
            const raw = await store.readDictFile(cleanName);
            if (!raw) {
              throw new Error(`本地词典缓存缺少文件：${cleanName}`);
            }
            return gunzipBytes(raw);
          },
        },
      });
      return builder.build();
    })().catch((err) => {
      tokenizerPromise = undefined;
      throw err;
    });
  }
  return tokenizerPromise;
}

self.addEventListener('message', (event: MessageEvent<FuriganaWorkerRequest>) => {
  const req = event.data;
  void (async () => {
    try {
      if (req.type === 'reset') {
        tokenizerPromise = undefined;
        const response: FuriganaWorkerResponse = { id: req.id, ok: true };
        self.postMessage(response);
        return;
      }

      const tokenizer = await getTokenizer();
      if (req.type === 'init') {
        const response: FuriganaWorkerResponse = { id: req.id, ok: true };
        self.postMessage(response);
        return;
      }

      if (req.type === 'tokenize') {
        const texts = req.texts ?? [];
        const tokensList: FuriganaWorkerMorpheme[][] = texts.map((text) =>
          tokenizer.tokenize(text).map((item) => ({
            surface_form: item.surface_form,
            ...(item.reading ? { reading: item.reading } : {}),
            ...(item.pos ? { pos: item.pos } : {}),
          })),
        );
        const response: FuriganaWorkerResponse = { id: req.id, ok: true, tokensList };
        self.postMessage(response);
      }
    } catch (error) {
      const response: FuriganaWorkerResponse = {
        id: req.id,
        ok: false,
        error: error instanceof Error ? error.message : String(error),
      };
      self.postMessage(response);
    }
  })();
});
