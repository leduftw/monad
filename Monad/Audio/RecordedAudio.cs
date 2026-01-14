namespace Monad.Audio;

public sealed record class RecordedAudio(short[] Pcm16Interleaved, int SampleRate, int Channels);
