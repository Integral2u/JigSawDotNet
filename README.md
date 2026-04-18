# JigSawDotNet

A lightweight .NET library that eliminates the runtime cost of conditional dispatch — switch statements, delegate indirection, and virtual calls — by assembling a purpose-built concrete type at startup whose abstract methods are **directly wired** to the chosen implementation via IL emission.

Optionally, JigSaw can profile every candidate implementation on the executing hardware and automatically select the fastest one.

---

## The Problem

A common pattern in performance-sensitive code is choosing between multiple implementations of the same algorithm at runtime:

```csharp
// Switch dispatch — pays branch prediction cost on every call
public int GetHash() => _method switch
{
    HashMethod.A => GetHashMethodA(),
    HashMethod.B => GetHashMethodB(),
    _ => 0
};

// Delegate dispatch — pays indirect call + null-check on every call
private readonly Func<int> _getHash;
public int GetHash() => _getHash();
```

Both approaches carry overhead on **every single call**. The branch or delegate indirection can never be eliminated by the JIT because the choice is expressed as a runtime condition that could theoretically change.

The deeper problem is that the *choice* is almost always made **once** — at construction time, config load, or startup — but the cost is paid on every hot-path invocation.

---

## The Solution

JigSaw moves the choice to a **one-time assembly step**. It uses `TypeBuilder` and raw IL emission to create a sealed concrete subclass where the abstract method slot is filled by a **direct copy of the chosen implementation's IL body**. From the JIT's perspective the method simply *is* that implementation — there is no wrapper, no branch, no delegate, no virtual dispatch overhead.

```
Traditional approach (paid on every call):
  GetHash() → switch/delegate → GetHashMethodA()   [two frames, branch cost]

JigSaw approach (paid once at startup):
  GetHash() → GetHashMethodA() body                [one frame, JIT can inline]
```

### Benchmark

Measured on N=1000 with BenchmarkDotNet:

| Method             | Mean     | Notes                                      |
|--------------------|----------|--------------------------------------------|
| MethodAViaDelegate | 1.348 µs | Indirect call + null check every iteration |
| MethodAViaSwitch   | 1.223 µs | Branch predicted but still present         |
| MethodAJigSaw      | 1.249 µs | Direct IL copy, no dispatch overhead       |
| MethodADirect      | 1.229 µs | Baseline — calling the method directly     |
| MethodCJigSaw      | 1.307 µs | System-selected best implementation        |

`MethodAJigSaw` is on par with `MethodADirect` — the JIT sees the same code.

---

## Installation

```
dotnet add package JigSawDotNet
```

---

## Quick Start

### 1. Annotate your abstract class

Mark the abstract method you want filled with `[PuzzlePlace]`, and each candidate implementation with `[PuzzlePeice]`, giving each a key/value pair that identifies when it should be selected:

```csharp
using JigSawDotNet;

public abstract class Hasher
{
    private readonly byte[] _data;

    public Hasher(byte[] data) => _data = data;

    // The slot to be filled
    [PuzzlePlace(nameof(ComputeHash))]
    public abstract int ComputeHash();

    // Candidate A
    [PuzzlePeice(nameof(ComputeHash), "Algorithm", "Polynomial")]
    public int HashPolynomial()
    {
        var result = 0;
        foreach (byte v in _data) result = (result * 31) + v;
        return result;
    }

    // Candidate B
    [PuzzlePeice(nameof(ComputeHash), "Algorithm", "Span")]
    public int HashSpan()
    {
        Span<byte> span = _data;
        var result = 0;
        for (var i = 0; i < span.Length; i++) result = (result * 31) + span[i];
        return result;
    }
}
```

### 2. Assemble and instantiate

**Explicit selection** — you know which implementation you want:

```csharp
// Assemble the type
var hasherType = Assembler.Assemble<Hasher>(new Dictionary<string, string>
{
    ["Algorithm"] = "Polynomial"
});
var hasher = (Hasher)Activator.CreateInstance(hasherType, data)!;

// Or in one step
var hasher = Assembler.CreateInstance<Hasher>(new Dictionary<string, string>
{
    ["Algorithm"] = "Polynomial"
}, data);
```

**Fluent API** — cleaner for multiple keys:

```csharp
var hasher = Assembler.For<Hasher>()
    .With("Algorithm", "Polynomial")
    .CreateInstance(data);
```

**System-tuned selection** — let JigSaw profile every candidate on the executing hardware and pick the fastest:

```csharp
var hasher = Assembler.CreateInstanceForSystem<Hasher>(
    getArgsFor: method => [],      // ComputeHash takes no arguments
    mapping:    [],                // no pinned constraints — try everything
    constructorArgs: [data]);

// Or pin some dimensions and let the rest be auto-selected
var hasher = Assembler.For<Hasher>()
    .With("Category", "Cryptographic")   // pinned
    .CreateInstanceForSystem(            // Algorithm auto-selected
        getArgsFor: method => [],
        constructorArgs: [data]);
```

`CreateInstanceForSystem` also reports which combination won:

```csharp
var hasher = Assembler.CreateInstanceForSystem<Hasher>(
    getArgsFor:      method => [],
    mapping:         [],
    warmup:          200,
    iterations:      2_000,
    bestCombination: out var winner,
    constructorArgs: [data]);

Console.WriteLine($"Best: {string.Join(", ", winner.Select(kv => $"{kv.Key}={kv.Value}"))}");
// Best: Algorithm=Span
```

---

## Use Cases

### System-specific optimisation

SIMD instruction availability, cache sizes, and memory bandwidth vary across hardware. An implementation that wins on a developer workstation may lose on a cloud VM. `CreateInstanceForSystem` profiles at process startup on the actual executing hardware and selects accordingly — no configuration required.

```csharp
// At startup — paid once
_compressor = Assembler.CreateInstanceForSystem<Compressor>(
    getArgsFor:      m => [samplePayload],
    mapping:         [],
    constructorArgs: [config]);

// In the hot path — zero dispatch cost
_compressor.Compress(buffer);
```

### User or configuration preferences

When the choice is driven by user settings or a config file, the mapping is known at startup and fixed for the process lifetime. JigSaw assembles the right type once and eliminates all subsequent branching:

```csharp
var serializer = Assembler.For<Serializer>()
    .With("Format",      config["format"])        // "Json" or "MessagePack"
    .With("Compression", config["compression"])   // "None" or "Brotli"
    .CreateInstance();
```

Without JigSaw, each call to `Serialize()` would evaluate these conditions. With JigSaw, the resulting type *is* the chosen combination — the conditions no longer exist in the call path.

### Eliminating delegate indirection

Storing `Func<T>` fields to avoid switch statements trades branch cost for indirect call cost. JigSaw eliminates both:

```csharp
// Before — delegate stored at construction, called indirectly forever
private readonly Func<int> _hash;
public int GetHash() => _hash();   // indirect call on every invocation

// After — JigSaw wires the implementation directly into the vtable slot
public abstract int GetHash();     // filled once at startup, direct call forever
```

### Feature flags

```csharp
var pipeline = Assembler.For<Pipeline>()
    .With("Logging",    flags["logging"]    ? "Enabled"  : "Disabled")
    .With("Validation", flags["validation"] ? "Strict"   : "Relaxed")
    .CreateInstance(config);
```

---

## Rules and Constraints

| Rule | Detail |
|------|--------|
| Base type must be `abstract` | JigSaw extends it with a sealed concrete subclass |
| `[PuzzlePlace]` must be on an `abstract` method | Non-abstract placement throws at assembly time |
| Exactly one `[PuzzlePeice]` must match per place | Zero or multiple matches throw at assembly time |
| Pieces must share the declaring type, return type, and parameter types with their place | Mismatched signatures are silently skipped |
| Assembly is cached | The same type + mapping combination is only built once |

---

## Introspection

Inspect what puzzle places and pieces exist on a type without assembling it:

```csharp
var puzzle = Assembler.GetJigSawPuzzle<Hasher>();

foreach (var (place, pieces) in puzzle)
{
    Console.WriteLine($"Place: {place.Name}");
    foreach (var piece in pieces)
    {
        var attr = piece.GetCustomAttribute<PuzzlePeice>()!;
        Console.WriteLine($"  [{attr.Key} = {attr.Value}] → {piece.Name}");
    }
}
```

Output:
```
Place: ComputeHash
  [Algorithm = Polynomial] → HashPolynomial
  [Algorithm = Span]       → HashSpan
```

---

## How It Works

1. **Reflection scan** — JigSaw reads `[PuzzlePlace]` and `[PuzzlePeice]` attributes on the base type and resolves which piece fills each place for the given mapping.
2. **Type emission** — A sealed subclass is built with `TypeBuilder`. Constructors are mirrored from the base class so all existing construction patterns continue to work.
3. **IL copy** — Rather than emitting a forwarding call, JigSaw copies the raw IL byte stream of the chosen piece directly into the override method, resolving all metadata tokens (methods, fields, types, strings, branches) into the new module. The JIT sees the body as local code and can inline freely.
4. **Access grants** — The dynamic assembly is granted `IgnoresAccessChecksTo` for the originating assembly so that private and internal members accessed by the copied IL remain accessible.
5. **Cache** — The assembled `Type` is stored in a static dictionary keyed on `FullName + mapping hash`. Repeated calls with the same mapping return the cached type instantly.

---

## API Reference

```csharp
// Assemble a Type from an explicit mapping
Type Assembler.Assemble<T>(Dictionary<string, string> mapping)

// Assemble and create an instance
T Assembler.CreateInstance<T>(Dictionary<string, string> mapping, params object?[]? args)

// Profile all combinations and return the fastest instance
T Assembler.CreateInstanceForSystem<T>(
    Func<MethodInfo, object?[]?> getArgsFor,
    Dictionary<string, string>   mapping,
    out Dictionary<string, string> bestCombination,
    params object?[]? constructorArgs)

// Profile with explicit warmup/iteration control
T Assembler.CreateInstanceForSystem<T>(
    Func<MethodInfo, object?[]?> getArgsFor,
    Dictionary<string, string>   mapping,
    int                          warmup,
    int                          iterations,
    out Dictionary<string, string> bestCombination,
    params object?[]? constructorArgs)

// Fluent builder entry point
AssemblerConfig<T> Assembler.For<T>()

// Introspect available places and pieces
Dictionary<MethodInfo, List<MethodInfo>> Assembler.GetJigSawPuzzle<T>()
```

---

## Requirements

- .NET 8 or later
- No external dependencies
