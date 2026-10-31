#!/usr/bin/env python3
"""Offline full stereo decode of the three already produced owned HTTP films.

No Server, credentials, project mutation or new export is involved.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import wave
import numpy as np


def verify(directory):
    with wave.open(str(directory / 'input.wav'), 'rb') as source:
        assert source.getnchannels() == 2 and source.getframerate() == 44100 and source.getsampwidth() == 2
        expected = np.frombuffer(source.readframes(source.getnframes()), dtype='<i2').reshape(-1, 2)
    observations = []
    assertions = 1
    for name, extension, count, lossless in [('ordinary', 'mp4', 132300, False), ('transparent', 'mov', 132300, True), ('ntsc', 'mov', 132432, True)]:
        media = directory / (name + '.' + extension)
        info = json.loads((directory / (name + '-independent-probe.json')).read_text())
        audio = next(value for value in info['streams'] if value['codec_type'] == 'audio')
        assert audio['channels'] == 2 and int(audio['sample_rate']) == 44100
        assert audio['codec_name'] == ('pcm_s16le' if lossless else 'aac')
        result = subprocess.run(['/usr/bin/ffmpeg', '-v', 'error', '-i', str(media), '-map', '0:a:0',
                                 '-c:a', 'pcm_s16le', '-f', 's16le', 'pipe:1'], capture_output=True, timeout=15, check=True)
        assert result.stderr == b'' and len(result.stdout) % 4 == 0
        decoded = np.frombuffer(result.stdout, dtype='<i2').reshape(-1, 2)
        assert abs(len(decoded) - count) <= (0 if lossless else 1024)
        assertions += 4
        matched = min(len(decoded), count)
        channels = []
        for channel in range(2):
            actual = decoded[:matched, channel].astype(np.float64)
            reference = expected[:matched, channel].astype(np.float64)
            correlation = float(np.corrcoef(actual, reference)[0, 1])
            rms = float(np.sqrt(np.mean(actual * actual)))
            reference_rms = float(np.sqrt(np.mean(reference * reference)))
            rmse = float(np.sqrt(np.mean((actual - reference) ** 2)))
            assert correlation > .99 and .95 < rms / reference_rms < 1.05
            assert rmse < 500
            if lossless:
                assert np.array_equal(decoded[:, channel], expected[:count, channel])
                assertions += 1
            assertions += 2
            channels.append({'channel': channel, 'correlationToOwnSourceChannel': correlation,
                             'rmsPcm16': rms, 'rootMeanSquaredErrorPcm16': rmse, 'exactPcmSamples': lossless})
        observations.append({'name': name, 'codec': audio['codec_name'], 'channels': channels,
                             'decodedFramesPerChannel': len(decoded), 'plannedFramesPerChannel': count,
                             'mediaSha256': hashlib.sha256(media.read_bytes()).hexdigest(),
                             'decodedPcmSha256': hashlib.sha256(result.stdout).hexdigest(), 'decoderExitCode': result.returncode})
    output = directory / 'offline-full-stereo-audio-verification.json'
    output.write_text(json.dumps({'succeeded': True, 'assertionCount': assertions, 'scope':
        'Independent offline complete stereo decode of existing owned HTTP films. Does not add HTTP requests or alter the original workflow/credential-scan counters.',
        'observations': observations}, indent=2) + '\n')
    print(json.dumps({'succeeded': True, 'assertions': assertions, 'films': len(observations)}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('directory', type=Path)
    verify(parser.parse_args().directory.resolve())
