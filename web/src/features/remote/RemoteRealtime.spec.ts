import { describe, expect, it } from 'vitest'
import { withStereo } from './RemoteRealtime'

describe('快速线路：answer 里声明能收双声道', () => {
  const sdp = (opus: string): string =>
    ['v=0', 'm=audio 9 UDP/TLS/RTP/SAVPF 111 63', 'a=rtpmap:111 opus/48000/2', opus, 'a=rtpmap:63 red/48000/2', 'a=fmtp:63 111/111',
      'm=video 9 UDP/TLS/RTP/SAVPF 125', 'a=rtpmap:125 H264/90000', 'a=fmtp:125 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f', ''].join('\r\n')

  it('给 Opus 的参数加上 stereo=1，别的行不动', () => {
    const out = withStereo(sdp('a=fmtp:111 minptime=10;useinbandfec=1'))
    expect(out).toContain('a=fmtp:111 minptime=10;useinbandfec=1;stereo=1\r\n')
    expect(out).toContain('a=fmtp:63 111/111\r\n')
    expect(out).toContain('a=fmtp:125 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f\r\n')
    expect(out.split('\r\n').length).toBe(sdp('x').split('\r\n').length)
  })

  it('已经有 stereo 的不再加', () => {
    const already = sdp('a=fmtp:111 minptime=10;stereo=1;useinbandfec=1')
    expect(withStereo(already)).toBe(already)
    const off = sdp('a=fmtp:111 minptime=10;useinbandfec=1;stereo=0')
    expect(withStereo(off)).toBe(off)
  })

  it('没有 Opus 参数行时原样返回', () => {
    expect(withStereo('v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 125\r\n')).toBe('v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 125\r\n')
    expect(withStereo('')).toBe('')
  })
})
