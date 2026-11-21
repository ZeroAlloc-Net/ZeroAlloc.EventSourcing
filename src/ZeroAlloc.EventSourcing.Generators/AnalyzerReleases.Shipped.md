; Shipped analyzer releases.
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

## Release 1.4.0

### New Rules

Rule ID | Category                            | Severity | Notes
--------|-------------------------------------|----------|-------------------------------------------------------------------
ZAES005 | ZeroAlloc.EventSourcing.Generators | Warning  | Containing type of an aggregate or projection is not partial
ZAES006 | ZeroAlloc.EventSourcing.Generators | Warning  | Generic aggregate or projection is not generated
ZAES007 | ZeroAlloc.EventSourcing.Generators | Error    | File-local aggregate or projection is not generated

## Release 2.0.0

### Changed Rules

Rule ID | New Category                        | New Severity | Old Category                        | Old Severity | Notes
--------|-------------------------------------|--------------|-------------------------------------|--------------|-------------------------------------------------------------
ZAES005 | ZeroAlloc.EventSourcing.Generators | Error        | ZeroAlloc.EventSourcing.Generators | Warning      | Containing type of an aggregate or projection is not partial
