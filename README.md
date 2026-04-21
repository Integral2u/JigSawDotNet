# JigSawDotNet

A lightweight .NET library that eliminates the runtime cost of conditional dispatch — switch statements, delegate indirection, and virtual calls — by assembling a purpose-built concrete type at startup whose abstract methods are **directly wired** to the chosen implementation via IL emission.

Optionally, JigSaw can profile every candidate implementation on the executing hardware and automatically select the fastest one.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

[View on NuGet](https://www.nuget.org/packages/JigSawDotNet/)

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

Both approaches carry overhead on **every single call**. The branch or delegate indirection can never be eliminated by the JIT because the choice is expressed as a runtime condition, even when that condition is effectively fixed for the lifetime of the process.

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
| MethodAViaDelegate | 6.800 us | Indirect call + null check every iteration |
| MethodAViaSwitch   | 6.782 us | Branch predicted but still present         |
| MethodAJigSaw      | 6.810 us | Direct IL copy, no dispatch overhead       |
| MethodADirect      | 6.815 us | Baseline — calling the method directly     |
| MethodBJigSaw      | 7.171 us | Direct IL copy, no dispatch overhead       |   
| MethodBDirect      | 7.043 us | Baseline — calling the method directly     | 
| MethodCJigSaw      | 6.757 us | System-selected best implementation        |


`MethodBJigSaw` lands on par with `MethodBDirect` — the JIT sees identical code.

---

## Installation

You can install the package via the .NET CLI:
```
dotnet add package JigSawDotNet --version 1.0.1
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

**Assemble the `Type` only** — useful when you manage instantiation yourself:

```csharp
Type hasherType = Assembler.Assemble<Hasher>(new Dictionary<string, string>
{
    ["Algorithm"] = "Polynomial"
});
var hasher = (Hasher)Activator.CreateInstance(hasherType, data)!;
```

**Assemble and create in one step:**

```csharp
var hasher = Assembler.CreateInstance<Hasher>(new Dictionary<string, string>
{
    ["Algorithm"] = "Polynomial"
}, data);
```

**System-tuned selection** — JigSaw profiles every candidate on the executing hardware and returns the fastest instance:

```csharp
var hasher = Assembler.CreateInstanceForSystem<Hasher>(
    getArgsFor:      method => [],   // ComputeHash() takes no arguments
    bestCombination: out var winner,
    constructorArgs: [data]);

Console.WriteLine($"Selected: {string.Join(", ", winner.Select(kv => $"{kv.Key}={kv.Value}"))}");
// Selected: Algorithm=Span
```

**System-tuned with a pinned constraint** — fix some keys and let JigSaw choose the rest:

```csharp
var hasher = Assembler.CreateInstanceForSystem<Hasher>(
    getArgsFor:      method => [],
    mapping:         new Dictionary<string, string> { ["Category"] = "Cryptographic" },
    bestCombination: out var winner,
    constructorArgs: [data]);
```

**System-tuned with explicit warmup and iteration control:**

```csharp
var hasher = Assembler.CreateInstanceForSystem<Hasher>(
    getArgsFor:      method => [],
    mapping:         [],
    warmup:          500,
    iterations:      5_000,
    bestCombination: out var winner,
    constructorArgs: [data]);
```

If a `[PuzzlePlace]` method takes arguments, provide them via `getArgsFor`:

```csharp
var hasher = Assembler.CreateInstanceForSystem<Hasher>(
    getArgsFor: method => method.Name switch
    {
        nameof(Hasher.ComputeHash)   => [],
        nameof(Hasher.TransformData) => [sampleInput],
        _                            => []
    },
    bestCombination: out _,
    constructorArgs: [data]);
```

---

## Use Cases

### System-specific optimisation

SIMD availability, cache sizes, and memory bandwidth vary across hardware. An implementation that wins on a developer workstation may lose on a cloud VM. `CreateInstanceForSystem` profiles at process startup on the actual hardware and selects accordingly — no configuration required.

```csharp
// At startup — profiled and assembled once
_compressor = Assembler.CreateInstanceForSystem<Compressor>(
    getArgsFor:      m => [samplePayload],
    bestCombination: out _,
    constructorArgs: [config]);

// In the hot path — zero dispatch cost
_compressor.Compress(buffer);
```

### User or configuration preferences

When the choice is driven by a config file or user settings, it is known at startup and fixed for the process lifetime. JigSaw assembles the right type once and eliminates all subsequent branching:

```csharp
var serializer = Assembler.CreateInstance<Serializer>(new Dictionary<string, string>
{
    ["Format"]      = config["format"],       // "Json" or "MessagePack"
    ["Compression"] = config["compression"]   // "None" or "Brotli"
});
```

Every call to `Serialize()` thereafter is a direct method call with no conditional logic in the path.

### Eliminating delegate indirection

Storing `Func<T>` fields to avoid switch statements trades branch cost for indirect call cost. JigSaw eliminates both:

```csharp
// Before — indirect call on every invocation
private readonly Func<int> _hash;
public int GetHash() => _hash();

// After — implementation wired directly into the vtable slot at startup
[PuzzlePlace(nameof(GetHash))]
public abstract int GetHash();
```

### Feature flags

```csharp
var pipeline = Assembler.CreateInstance<Pipeline>(new Dictionary<string, string>
{
    ["Logging"]    = flags.Logging    ? "Enabled"  : "Disabled",
    ["Validation"] = flags.Validation ? "Strict"   : "Relaxed"
});
```

---

## Rules and Constraints

| Rule | Detail |
|------|--------|
| Base type must be `abstract` | JigSaw extends it with a sealed concrete subclass |
| `[PuzzlePlace]` must be on an `abstract` method | Non-abstract placement throws at assembly time |
| `[PuzzlePeice]` must match place | Zero matches throw at assembly time |
| Pieces must share the declaring type, return type, and parameter types with their place | Mismatched signatures are silently skipped as non-candidates |
| Assembly is cached | The same type + mapping combination is only built once per process |

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
4. **Access grants** — The dynamic assembly declares `IgnoresAccessChecksTo` for the originating assembly so that private and internal members accessed by the copied IL remain accessible.
5. **Cache** — The assembled `Type` is stored keyed on `FullName + mapping hash`. Repeated calls with the same mapping return the cached type instantly.

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
    out Dictionary<string, string> bestCombination,
    params object?[]? constructorArgs)

// Profile with some keys pinned as constraints
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

// Introspect available places and pieces without assembling
Dictionary<MethodInfo, List<MethodInfo>> Assembler.GetJigSawPuzzle<T>()
```

---

## Requirements

- .NET 8 or later
- No external dependencies
