namespace Monad.Audio;

/// <summary>Sample rate and channel count of an interleaved PCM stream.</summary>
public readonly record struct AudioFormat(int SampleRate, int Channels)
{
    public bool IsValid => this.SampleRate > 0 && this.Channels > 0;

    public override string ToString() => $"{this.SampleRate} Hz, {this.Channels} ch";
}
