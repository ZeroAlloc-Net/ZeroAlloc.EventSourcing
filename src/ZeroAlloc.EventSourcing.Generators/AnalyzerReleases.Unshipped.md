; Unshipped analyzer release.
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### Changed Rules

Rule ID | New Category                        | New Severity | Old Category                        | Old Severity | Notes
--------|-------------------------------------|--------------|-------------------------------------|--------------|-------------------------------------------------------------
ZAES005 | ZeroAlloc.EventSourcing.Generators | Error        | ZeroAlloc.EventSourcing.Generators | Warning      | Containing type of an aggregate or projection is not partial
