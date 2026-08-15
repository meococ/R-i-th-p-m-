$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding $false

# --- XAML ---
$xamlPath = Resolve-Path 'Views\AbutmentRebarView.xaml'
$xaml = [IO.File]::ReadAllText($xamlPath, [Text.Encoding]::UTF8)
$old = @'
                                        <ComboBox x:Name="ZoneSelector"
                                                  ItemsSource="{Binding AvailableZones}"
                                                  SelectedValuePath="Kind"
                                                  SelectedValue="{Binding SelectedZoneKind, Mode=TwoWay}"
'@
$new = @'
                                        <ComboBox x:Name="ZoneSelector"
                                                  ItemsSource="{Binding AvailableZones}"
                                                  SelectedItem="{Binding SelectedZoneOption, Mode=TwoWay}"
'@
if ($xaml.Contains('SelectedValuePath="Kind"')) {
    $xaml2 = $xaml.Replace('SelectedValuePath="Kind"', '')
    $xaml2 = $xaml2.Replace('SelectedValue="{Binding SelectedZoneKind, Mode=TwoWay}"', 'SelectedItem="{Binding SelectedZoneOption, Mode=TwoWay}"')
    [IO.File]::WriteAllText($xamlPath, $xaml2, $utf8)
    Write-Host 'XAML patched'
} else {
    Write-Host 'XAML already without SelectedValuePath or different'
}

# --- ViewModel ---
$vmPath = Resolve-Path 'ViewModels\AbutmentRebarViewModel.cs'
$lines = [IO.File]::ReadAllLines($vmPath)
$out = New-Object System.Collections.Generic.List[string]
$i = 0
$insertedField = $false
$insertedProp = $false
while ($i -lt $lines.Count) {
    $line = $lines[$i]

    if (-not $insertedField -and $line -match 'private AbutmentZoneKind _selectedZoneKind') {
        $out.Add($line)
        $out.Add('    private AbutmentZoneOptionViewModel? _selectedZoneOption;')
        $insertedField = $true
        $i++; continue
    }

    # Replace SelectedZoneKind property body to also sync option
    if ($line -match 'public AbutmentZoneKind SelectedZoneKind') {
        $out.Add('    public AbutmentZoneKind SelectedZoneKind')
        $out.Add('    {')
        $out.Add('        get => _selectedZoneKind;')
        $out.Add('        set')
        $out.Add('        {')
        $out.Add('            if (!SetProperty(ref _selectedZoneKind, value))')
        $out.Add('                return;')
        $out.Add('            SyncSelectedZoneOptionFromKind();')
        $out.Add('            OnZoneSelectionChanged(value);')
        $out.Add('        }')
        $out.Add('    }')
        $out.Add('')
        $out.Add('    public AbutmentZoneOptionViewModel? SelectedZoneOption')
        $out.Add('    {')
        $out.Add('        get => _selectedZoneOption;')
        $out.Add('        set')
        $out.Add('        {')
        $out.Add('            if (SetProperty(ref _selectedZoneOption, value) && value is not null &&')
        $out.Add('                value.Kind != _selectedZoneKind)')
        $out.Add('            {')
        $out.Add('                // ComboBox drives Kind; avoid re-entry loops.')
        $out.Add('                _selectedZoneKind = value.Kind;')
        $out.Add('                OnPropertyChanged(nameof(SelectedZoneKind));')
        $out.Add('                OnZoneSelectionChanged(value.Kind);')
        $out.Add('            }')
        $out.Add('        }')
        $out.Add('    }')
        $insertedProp = $true
        # skip old property block until closing brace of property
        $i++
        # skip until we've consumed old getter/setter property
        $depth = 0
        $started = $false
        while ($i -lt $lines.Count) {
            $l = $lines[$i]
            foreach ($ch in $l.ToCharArray()) {
                if ($ch -eq '{') { $depth++; $started = $true }
                elseif ($ch -eq '}') { $depth-- }
            }
            $i++
            if ($started -and $depth -le 0) { break }
        }
        continue
    }

    # Replace RefreshZoneOptions method
    if ($line -match 'private void RefreshZoneOptions\(\)') {
        $out.Add('    private void RefreshZoneOptions()')
        $out.Add('    {')
        $out.Add('        var classification = _controller.Classification;')
        $out.Add('        var configuredZones = _controller.Preset.Rules')
        $out.Add('            .Where(rule => rule.Enabled)')
        $out.Add('            .Select(rule => rule.Zone)')
        $out.Add('            .Distinct()')
        $out.Add('            .OrderBy(ZoneSortOrder)')
        $out.Add('            .ToList();')
        $out.Add('        if (configuredZones.Count == 0)')
        $out.Add('            configuredZones.Add(AbutmentZoneKind.Footing);')
        $out.Add('')
        $out.Add('        var recognized = classification?.Zones.Select(zone => zone.Kind).ToHashSet()')
        $out.Add('            ?? new HashSet<AbutmentZoneKind>();')
        $out.Add('')
        $out.Add('        var preferred = _selectedZoneKind;')
        $out.Add('        AvailableZones.Clear();')
        $out.Add('        foreach (var zone in configuredZones)')
        $out.Add('        {')
        $out.Add('            var isRecognized = recognized.Contains(zone);')
        $out.Add('            var status = isRecognized')
        $out.Add('                ? "Da nhan dien geometry • co rule duoc duyet"')
        $out.Add('                : "Chua nhan dien duoc canonical geometry";')
        $out.Add('            AvailableZones.Add(new AbutmentZoneOptionViewModel(')
        $out.Add('                zone,')
        $out.Add('                ZoneDisplayName(zone),')
        $out.Add('                status,')
        $out.Add('                isRecognized,')
        $out.Add('                hasEnabledRules: true));')
        $out.Add('        }')
        $out.Add('')
        $out.Add('        if (AvailableZones.Count > 0 && AvailableZones.All(item => item.Kind != preferred))')
        $out.Add('            preferred = AvailableZones[0].Kind;')
        $out.Add('')
        $out.Add('        // Rebind ComboBox SelectedItem after ItemsSource rebuild (WPF blank selection fix).')
        $out.Add('        _selectedZoneKind = preferred;')
        $out.Add('        _selectedZoneOption = AvailableZones.FirstOrDefault(item => item.Kind == preferred);')
        $out.Add('        OnPropertyChanged(nameof(SelectedZoneKind));')
        $out.Add('        OnPropertyChanged(nameof(SelectedZoneOption));')
        $out.Add('        OnPropertyChanged(nameof(HasCanonicalZone));')
        $out.Add('    }')
        $out.Add('')
        $out.Add('    private void SyncSelectedZoneOptionFromKind()')
        $out.Add('    {')
        $out.Add('        var match = AvailableZones.FirstOrDefault(item => item.Kind == _selectedZoneKind);')
        $out.Add('        if (!ReferenceEquals(_selectedZoneOption, match))')
        $out.Add('        {')
        $out.Add('            _selectedZoneOption = match;')
        $out.Add('            OnPropertyChanged(nameof(SelectedZoneOption));')
        $out.Add('        }')
        $out.Add('    }')
        # skip old method
        $i++
        $depth = 0
        $started = $false
        while ($i -lt $lines.Count) {
            $l = $lines[$i]
            foreach ($ch in $l.ToCharArray()) {
                if ($ch -eq '{') { $depth++; $started = $true }
                elseif ($ch -eq '}') { $depth-- }
            }
            $i++
            if ($started -and $depth -le 0) { break }
        }
        continue
    }

    # Fix places that set _selectedZoneKind directly without option sync
    if ($line -match '_selectedZoneKind = AbutmentZoneKind\.Footing;' -and $lines[$i+1] -match '_suppressRuleChanges = false') {
        $out.Add('                        _selectedZoneKind = AbutmentZoneKind.Footing;')
        $out.Add('                        SyncSelectedZoneOptionFromKind();')
        $out.Add('                        OnPropertyChanged(nameof(SelectedZoneKind));')
        $i++
        continue
    }

    $out.Add($line)
    $i++
}

[IO.File]::WriteAllText($vmPath, ($out -join "`r`n") + "`r`n", $utf8)
Write-Host "insertedField=$insertedField insertedProp=$insertedProp"
Select-String -Path $vmPath -Pattern 'SelectedZoneOption|SyncSelectedZoneOptionFromKind|RefreshZoneOptions' |
  Select-Object -First 30 |
  ForEach-Object { "{0}:{1}" -f $_.LineNumber, $_.Line.Trim() }
