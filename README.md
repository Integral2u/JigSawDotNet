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
| MethodAViaDelegate | 1.348 µs | Indirect call + null check every iteration |
| MethodAViaSwitch   | 1.223 µs | Branch predicted but still present         |
| MethodAJigSaw      | 1.249 µs | Direct IL copy, no dispatch overhead       |
| MethodADirect      | 1.229 µs | Baseline — calling the method directly     |
| MethodCJigSaw      | 1.307 µs | System-selected best implementation        |

`MethodAJigSaw` lands on par with `MethodADirect` — the JIT sees identical code.

---

## Installation

```
dotnet add package JigSawDotNet
```

---

## Attributes

JigSaw uses three attributes to describe the puzzle. Two mark the **abstract slot** to be filled (`[PuzzlePlace]` and `[PuzzleCornerPiece]`), and one marks each **candidate implementation** (`[PuzzlePeice]`).

### `[PuzzlePeice]`

Marks a method as a candidate implementation for a named abstract slot.

```csharp
[PuzzlePeice(pointer, key, value)]
```

| Parameter | Description |
|-----------|-------------|
| `pointer` | Name of the abstract slot this piece can fill |
| `key`     | The mapping key that selects this piece |
| `value`   | The mapping value that selects this piece |

A piece is selected when `mapping[key] == value`.

---

### `[PuzzlePlace]`

Marks an abstract method as a slot to be filled by a `[PuzzlePeice]` from within the same class, or optionally from external assemblies.

```csharp
[PuzzlePlace(pointer, allowStaticExternal = false, handshake = null)]
```

| Parameter            | Default | Description |
|----------------------|---------|-------------|
| `pointer`            | —       | Identifier that links this place to its pieces. Typically `nameof(TheMethod)` |
| `allowStaticExternal`| `false` | When `true`, JigSaw also scans all referenced assemblies for `[PuzzlePeice]` methods that match this place's pointer, return type, and parameter types |
| `handshake`          | `null`  | Name of a `static bool` method on the declaring type. When set, any external piece candidate must pass this check before being accepted |

**Basic usage:**

```csharp
[PuzzlePlace(nameof(ComputeHash))]
public abstract int ComputeHash();
```

**With external pieces allowed:**

```csharp
[PuzzlePlace(nameof(ComputeHash), allowStaticExternal: true)]
public abstract int ComputeHash();
```

**With a handshake to validate external pieces:**

```csharp
[PuzzlePlace(nameof(ComputeHash), allowStaticExternal: true, handshake: nameof(ValidatePiece))]
public abstract int ComputeHash();

// Receives the candidate MethodInfo — return true to accept, false to reject
public static bool ValidatePiece(MethodInfo candidate) =>
    candidate.GetCustomAttribute<MyRequiredAttribute>() is not null;
```

The handshake runs at assembly time, not on every call. Use it to enforce contracts on external pieces — required attributes, naming conventions, security policies, etc.

---

### `[PuzzleCornerPiece]`

A self-contained alternative to `[PuzzlePlace]` + `[PuzzlePeice]` for cases where the available implementations are known at design time and expressed as a fixed lookup table directly on the abstract method. The attribute itself carries both the slot declaration and the full list of options.

```csharp
// Simple form — no external pieces
[PuzzleCornerPiece(pointer, key1, method1, key2, method2, ...)]

// Extended form — with external pieces and handshake
[PuzzleCornerPiece(pointer, allowStaticExternal, handshake, key1, method1, key2, method2, ...)]
```

| Parameter            | Default | Description |
|----------------------|---------|-------------|
| `pointer`            | —       | Identifier for this slot. The mapping key must equal this value when `allowStaticExternal` is `false` |
| `allowStaticExternal`| `false` | When `true`, JigSaw first scans external assemblies for `[PuzzlePeice]` methods that match this slot, falling back to the built-in key/method table if none are found |
| `handshake`          | `null`  | Name of a `static bool` method on the declaring type. External piece candidates must pass this check |
| `key, method` pairs  | —       | Flat list of alternating option names and target method names or fully-qualified static method paths |

The key/method pairs map a **mapping value** to a method to use. The method can be:
- A simple name resolved on the declaring type: `"MyMethod"`
- A fully-qualified static path: `"My.Namespace.SomeClass.MyMethod"`

**Simple form — fixed internal options:**

```csharp
public abstract class MathOps
{
    [PuzzleCornerPiece(nameof(Add),
        "AddInternal",  "AddInternal",                        // mapping value → method on this type
        "AddExternal",  "My.Other.Assembly.ExternalMath.Add"  // mapping value → external static method
    )]
    public abstract int Add(int a, int b);

    public static int AddInternal(int a, int b) => a + b;
}

// Usage — the mapping key matches the pointer name
var ops = Assembler.CreateInstance<MathOps>(
    new Dictionary<string, string> { ["Add"] = "AddInternal" });
```

**Extended form — external pieces via `[PuzzlePeice]` with a handshake:**

```csharp
public abstract class Processor
{
    // AllowStaticExternal=true: any assembly can contribute a [PuzzlePeice] for "Process"
    // Handshake: only accept pieces that declare [ApprovedAlgorithm]
    [PuzzleCornerPiece("Process",
        allowStaticExternal: true,
        handshake:           nameof(IsApproved),
        "Fallback",          "FallbackProcess"   // built-in option if no external piece matches
    )]
    public abstract byte[] Process(byte[] data);

    public static byte[] FallbackProcess(byte[] data) => data;

    public static bool IsApproved(MethodInfo candidate) =>
        candidate.GetCustomAttribute<ApprovedAlgorithmAttribute>() is not null;
}
```

When `allowStaticExternal: true`, JigSaw resolves in this order:
1. Scan all referenced assemblies for a `[PuzzlePeice]` whose `pointer`, return type, and parameter types match, and whose `key/value` matches the current mapping — validated by the handshake if one is set
2. If no external piece matches, fall back to the built-in key/method table using the mapping value

---

## Quick Start

### Basic — pieces defined on the same class

```csharp
using JigSawDotNet;

public abstract class Hasher
{
    private readonly byte[] _data;

    public Hasher(byte[] data) => _data = data;

    [PuzzlePlace(nameof(ComputeHash))]
    public abstract int ComputeHash();

    [PuzzlePeice(nameof(ComputeHash), "Algorithm", "Polynomial")]
    public int HashPolynomial()
    {
        var result = 0;
        foreach (byte v in _data) result = (result * 31) + v;
        return result;
    }

    [PuzzlePeice(nameof(ComputeHash), "Algorithm", "Span")]
    public int HashSpan()
    {
        Span<byte> span = _data;
        var result = 0;
        for (var i = 0; i < span.Length; i++) result = (result * 31) + span[i];
        return result;
    }
}

var hasher = Assembler.CreateInstance<Hasher>(new Dictionary<string, string>
{
    ["Algorithm"] = "Polynomial"
}, data);
```

### External pieces via `AllowStaticExternal`

Pieces can live in a completely separate assembly. This is useful for plugin architectures or when implementations are provided by downstream packages.

```csharp
// In your core library
public abstract class Hasher
{
    private readonly byte[] _data;
    public Hasher(byte[] data) => _data = data;

    [PuzzlePlace(nameof(ComputeHash), allowStaticExternal: true, handshake: nameof(Verify))]
    public abstract int ComputeHash();

    // Only accept external pieces that carry [CertifiedHash]
    public static bool Verify(MethodInfo candidate) =>
        candidate.GetCustomAttribute<CertifiedHashAttribute>() is not null;
}

// In a plugin assembly — no reference back to Hasher required, only to JigSawDotNet
public static class FastHashPlugin
{
    [PuzzlePeice(nameof(Hasher.ComputeHash), "Algorithm", "Native")]
    [CertifiedHash]
    public static int NativeHash(byte[] data)
    {
        // ... hardware-accelerated implementation
    }
}

// At startup — picks up the plugin piece automatically
var hasher = Assembler.CreateInstance<Hasher>(new Dictionary<string, string>
{
    ["Algorithm"] = "Native"
}, data);
```

### Assemble only

Useful when you manage construction yourself:

```csharp
Type hasherType = Assembler.Assemble<Hasher>(new Dictionary<string, string>
{
    ["Algorithm"] = "Polynomial"
});
var hasher = (Hasher)Activator.CreateInstance(hasherType, data)!;
```

### System-tuned selection

JigSaw profiles every valid combination on the executing hardware and returns the fastest:

```csharp
var hasher = Assembler.CreateInstanceForSystem<Hasher>(
    getArgsFor:      method => [],   // ComputeHash() takes no arguments
    bestCombination: out var winner,
    constructorArgs: [data]);

Console.WriteLine($"Selected: {string.Join(", ", winner.Select(kv => $"{kv.Key}={kv.Value}"))}");
// Selected: Algorithm=Span
```

With a pinned constraint — fix some keys and let JigSaw choose the rest:

```csharp
var hasher = Assembler.CreateInstanceForSystem<Hasher>(
    getArgsFor:      method => [],
    mapping:         new Dictionary<string, string> { ["Category"] = "Cryptographic" },
    bestCombination: out var winner,
    constructorArgs: [data]);
```

With explicit warmup and iteration control:

```csharp
var hasher = Assembler.CreateInstanceForSystem<Hasher>(
    getArgsFor:      method => [],
    mapping:         [],
    warmup:          500,
    iterations:      5_000,
    bestCombination: out var winner,
    constructorArgs: [data]);
```

If a place method takes arguments, provide them via `getArgsFor`:

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
_compressor = Assembler.CreateInstanceForSystem<Compressor>(
    getArgsFor:      m => [samplePayload],
    bestCombination: out _,
    constructorArgs: [config]);

// In the hot path — zero dispatch cost
_compressor.Compress(buffer);
```

### User or configuration preferences

```csharp
var serializer = Assembler.CreateInstance<Serializer>(new Dictionary<string, string>
{
    ["Format"]      = config["format"],       // "Json" or "MessagePack"
    ["Compression"] = config["compression"]   // "None" or "Brotli"
});
```

Every call to `Serialize()` thereafter is a direct method call with no conditional logic in the path.

### Plugin architectures

`AllowStaticExternal` allows downstream packages to contribute implementations without a reference back to the declaring assembly. Combined with a `handshake`, you can enforce contracts on what those plugins are permitted to supply.

```csharp
// Core library declares the slot and the contract
[PuzzlePlace(nameof(Render), allowStaticExternal: true, handshake: nameof(IsCompatible))]
public abstract void Render(Scene scene);

public static bool IsCompatible(MethodInfo m) =>
    m.GetCustomAttribute<RendererApiVersionAttribute>()?.Version >= 3;

// Plugin packages contribute [PuzzlePeice] methods — discovered automatically at runtime
```

### Eliminating delegate indirection

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
| `[PuzzlePlace]` and `[PuzzleCornerPiece]` must be on `abstract` methods | Non-abstract placement throws at assembly time |
| Exactly one `[PuzzlePeice]` must match per `[PuzzlePlace]` | Zero or multiple matches throw at assembly time |
| Pieces must match their place's return type and parameter types | Mismatched signatures are silently skipped |
| `[PuzzleCornerPiece]` mapping key must equal the pointer | When `allowStaticExternal` is `false` |
| Handshake method must be `static bool` on the declaring type | Receives the candidate `MethodInfo`, returns `true` to accept |
| Avoid primary constructors on base types | The C# compiler synthesizes capture fields (`<param>P`) that produce unpredictable IL. Use traditional constructors with explicit field assignment |
| Assembly is cached | The same type + mapping combination is only built once per process |

---

## Introspection

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

1. **Reflection scan** — JigSaw reads `[PuzzlePlace]`, `[PuzzleCornerPiece]`, and `[PuzzlePeice]` attributes on the base type. If any place sets `allowStaticExternal: true`, referenced assemblies are also scanned for matching external pieces.
2. **Handshake** — External piece candidates are passed to the declaring type's handshake method (if set). Candidates that return `false` are rejected before assembly begins.
3. **Type emission** — A sealed subclass is built with `TypeBuilder`. Constructors are mirrored from the base class so all existing construction patterns continue to work.
4. **IL copy** — Rather than emitting a forwarding call, JigSaw copies the raw IL byte stream of the chosen piece directly into the override method, resolving all metadata tokens (methods, fields, types, strings, branches) into the new module. The JIT sees the body as local code and can inline freely. Static external pieces use a forwarding call instead since they live in a separate module.
5. **Access grants** — The dynamic assembly declares `IgnoresAccessChecksTo` for the originating assembly so that private and internal members accessed by the copied IL remain accessible.
6. **Cache** — The assembled `Type` is stored keyed on `FullName + mapping hash`. Repeated calls with the same mapping return the cached type instantly. The cache can be disabled via `Assembler.Cache = false` (useful in tests).

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

// Enable or disable the type cache (default: true)
bool Assembler.Cache { get; set; }
```

---

## Requirements

- .NET 8 or later
- No external dependencies