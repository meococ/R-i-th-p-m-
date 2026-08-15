$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom('C:\Program Files\Autodesk\Revit 2025\RevitAPI.dll')
$type = $assembly.GetType('Autodesk.Revit.DB.Structure.RebarHookType')
$type.GetMethods([Reflection.BindingFlags]'Public,Static,Instance,DeclaredOnly') |
    Select-Object Name, IsStatic, @{ Name = 'Signature'; Expression = { $_.ToString() } } |
    Format-Table -AutoSize
