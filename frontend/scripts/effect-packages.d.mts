export function sampleEffectsDir(): string | null;
export function packSampleEffect(name: string): Uint8Array<ArrayBuffer>;
export function packVariantEffect(variant: 'WordEmphasisCurves' | 'SpriteTrailImages'): Uint8Array<ArrayBuffer>;
export function zipFiles(files: { name: string; data: Uint8Array }[]): Uint8Array<ArrayBuffer>;
