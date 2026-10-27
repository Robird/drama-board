using System.Collections.Immutable;
using DramaBoard.Protocol;

namespace DramaBoard.Server.FreePlay.Runtime;

/// <summary>Represents one immutable, value-comparable buffer of instructions that have not started yet.</summary>
internal sealed class InstructionBuffer : IEquatable<InstructionBuffer> {
    private readonly ImmutableArray<Instruction> items;

    /// <summary>Initializes a buffer from an already frozen instruction sequence.</summary>
    private InstructionBuffer(ImmutableArray<Instruction> items) {
        this.items = items;
    }

    /// <summary>Initializes a buffer by validating and copying the given instructions.</summary>
    private InstructionBuffer(IEnumerable<Instruction> instructions, string parameterName) {
        ArgumentNullException.ThrowIfNull(instructions);
        ImmutableArray<Instruction>.Builder builder = ImmutableArray.CreateBuilder<Instruction>();
        foreach (Instruction instruction in instructions) {
            if (instruction is null) {
                throw new ArgumentException("Instruction buffers cannot contain null entries.", parameterName);
            }

            builder.Add(instruction);
        }

        this.items = builder.ToImmutable();
    }

    /// <summary>Gets the empty instruction buffer.</summary>
    public static InstructionBuffer Empty { get; } = new(ImmutableArray<Instruction>.Empty);

    /// <summary>Creates one buffer that holds a frozen snapshot of the given instructions.</summary>
    /// <param name="instructions">The instructions to snapshot; later changes to the source are not observed.</param>
    /// <returns>The frozen instruction buffer.</returns>
    public static InstructionBuffer Of(IEnumerable<Instruction> instructions) => new(instructions, nameof(instructions));

    /// <summary>Gets a value indicating whether the buffer holds no instructions.</summary>
    public bool IsEmpty => this.items.IsEmpty;

    /// <summary>Gets the first instruction that has not started yet, or null when the buffer is empty.</summary>
    public Instruction? Head => this.IsEmpty ? null : this.items[0];

    /// <summary>Gets the frozen instruction sequence for inspection.</summary>
    public IReadOnlyList<Instruction> Items => this.items;

    /// <summary>Returns a buffer without its first instruction, keeping the remaining tail unchanged.</summary>
    /// <returns>The remaining tail as one new buffer.</returns>
    /// <exception cref="InvalidOperationException">The buffer is empty.</exception>
    public InstructionBuffer Tail() {
        if (this.IsEmpty) {
            throw new InvalidOperationException("Cannot take the tail of an empty instruction buffer.");
        }

        return new InstructionBuffer(this.items.RemoveAt(0));
    }

    /// <summary>Returns whether the other buffer holds the same instruction sequence.</summary>
    /// <param name="other">The buffer to compare with.</param>
    /// <returns>Whether both buffers hold the same element sequence.</returns>
    public bool Equals(InstructionBuffer? other) {
        if (other is null || this.items.Length != other.items.Length) {
            return false;
        }

        for (int index = 0; index < this.items.Length; index++) {
            if (!EqualityComparer<Instruction>.Default.Equals(this.items[index], other.items[index])) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns whether the given object is an instruction buffer with the same element sequence.</summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns>Whether the object is an equal instruction buffer.</returns>
    public override bool Equals(object? obj) => obj is InstructionBuffer other && this.Equals(other);

    /// <summary>Returns a hash code derived from the element sequence.</summary>
    /// <returns>The combined hash code of all elements.</returns>
    public override int GetHashCode() {
        HashCode hash = default;
        foreach (Instruction instruction in this.items) {
            hash.Add(instruction);
        }

        return hash.ToHashCode();
    }

    /// <summary>Returns whether both buffers hold the same element sequence.</summary>
    /// <param name="left">The left buffer.</param>
    /// <param name="right">The right buffer.</param>
    /// <returns>Whether both buffers hold the same element sequence.</returns>
    public static bool operator ==(InstructionBuffer? left, InstructionBuffer? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Returns whether the buffers differ in their element sequences.</summary>
    /// <param name="left">The left buffer.</param>
    /// <param name="right">The right buffer.</param>
    /// <returns>Whether the buffers differ in their element sequences.</returns>
    public static bool operator !=(InstructionBuffer? left, InstructionBuffer? right) => !(left == right);
}
