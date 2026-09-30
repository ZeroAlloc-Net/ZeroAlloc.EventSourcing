; Unshipped analyzer release.
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category                            | Severity | Notes
--------|-------------------------------------|----------|-------------------------------------------------------------------
ZAES005 | ZeroAlloc.EventSourcing.Generators | Warning  | Containing type of an aggregate or projection is not partial
ZAES006 | ZeroAlloc.EventSourcing.Generators | Warning  | Generic aggregate or projection is not generated
