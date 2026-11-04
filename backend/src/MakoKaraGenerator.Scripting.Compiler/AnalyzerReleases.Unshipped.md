; Unshipped analyzer release

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
MKV2001 | ScriptV2Purity | Error | Reject capabilities outside fixed inputs
MKV2002 | ScriptV2Purity | Error | Reject mutable static state
MKV2003 | ScriptV2Purity | Error | Reject external state mutation
MKV2004 | ScriptV2Purity | Error | Require recursively readonly prepared results
MKV2005 | ScriptV2Purity | Error | Reject stage handle escape
MKV2006 | ScriptV2Purity | Error | Reject calls outside the supported subset
MKV2007 | ScriptV2Purity | Error | Reject unsupported execution capabilities
MKV2008 | ScriptV2Performance | Warning | Report per-frame allocation
MKV2009 | ScriptV2Performance | Warning | Report per-frame LINQ
