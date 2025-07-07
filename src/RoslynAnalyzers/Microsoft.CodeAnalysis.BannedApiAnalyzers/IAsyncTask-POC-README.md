# IAsyncTask Banned API Analyzer - POC

This is a proof-of-concept implementation that extends the Roslyn Banned API Analyzer to ban APIs only when used within implementations of `Microsoft.Build.Framework.IAsyncTask.ExecuteAsync()` methods.

## How It Works

### Key Components

1. **`IAsyncTaskBannedAnalyzer<TSyntaxKind>`** - Base analyzer class that detects when code is executing within an `ExecuteAsync()` implementation
2. **`CSharpIAsyncTaskBannedAnalyzer`** - C# language-specific implementation
3. **`BasicIAsyncTaskBannedAnalyzer.vb`** - VB.NET language-specific implementation

### Configuration

Instead of using `BannedSymbols.txt`, this analyzer uses `IAsyncTaskBannedApis.txt` with the same format:

```txt
# Console APIs - use structured logging instead
M:System.Console.WriteLine(System.String);Use ILogger instead
M:System.Console.Write(System.String);Use ILogger instead

# File.ReadAllText - use async versions
M:System.IO.File.ReadAllText(System.String);Use File.ReadAllTextAsync instead

# Thread.Sleep - don't block in async methods
M:System.Threading.Thread.Sleep(System.Int32);Use Task.Delay instead in async methods
```

### Detection Logic

The analyzer:

1. **Identifies IAsyncTask implementations** - Checks if a type implements `Microsoft.Build.Framework.IAsyncTask`
2. **Locates ExecuteAsync methods** - Finds methods with signature `Task<bool> ExecuteAsync()`
3. **Context-aware analysis** - Only reports banned APIs when they're used within `ExecuteAsync()` implementations
4. **Handles various implementation patterns**:
   - Implicit interface implementation
   - Explicit interface implementation  
   - Inheritance chains

### Example Usage

```csharp
using System;
using System.Threading.Tasks;
using Microsoft.Build.Framework;

public class MyTask : IAsyncTask
{
    public async Task<bool> ExecuteAsync()
    {
        // This WILL trigger the diagnostic if Console.WriteLine is banned
        Console.WriteLine("Hello World"); // ← Banned API usage detected
        return true;
    }
    
    public void SomeOtherMethod()
    {
        // This will NOT trigger the diagnostic
        Console.WriteLine("Hello World"); // ← Same API, but not in ExecuteAsync
    }
}
```

### Technical Implementation Details

#### Method Context Detection

The analyzer uses the Roslyn operation tree to walk up from any API usage and determine if it's within an `ExecuteAsync()` method:

```csharp
private static IMethodSymbol? GetContainingMethod(IOperation operation)
{
    // Walk up operation tree to find containing method
    // Check semantic model to get method symbol
    // Verify it matches ExecuteAsync signature
}
```

#### Interface Implementation Verification

```csharp
private bool IsExecuteAsyncImplementation(IMethodSymbol methodSymbol)
{
    // 1. Check method name is "ExecuteAsync"
    // 2. Check return type is Task<bool>
    // 3. Check containing type implements IAsyncTask
    // 4. Handle explicit interface implementations
}
```

### Benefits

- **Granular control** - Ban APIs only in specific method contexts
- **Architecture enforcement** - Ensure async best practices in MSBuild tasks
- **Backward compatible** - Doesn't interfere with existing banned API files
- **Extensible** - Can be modified to target other interfaces/methods

### Diagnostic ID

The POC uses diagnostic ID `RS0036` to avoid conflicts with existing rules.

### Next Steps

This POC demonstrates the feasibility of method-level API banning. To make it production-ready:

1. **Add configuration parsing** - Support interface/method specifications in config files
2. **Improve performance** - Cache method lookups and interface checks
3. **Add more language support** - Complete VB.NET implementation
4. **Handle edge cases** - Async lambdas, local functions, etc.
5. **Add comprehensive tests** - Cover all implementation patterns
