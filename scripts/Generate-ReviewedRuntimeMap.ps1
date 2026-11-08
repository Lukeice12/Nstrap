param([string]$EvidenceRoot='J:\SKILLHELPER\nstrap-investigation')
$ErrorActionPreference='Stop'
function Read-Proof([string]$path) {
    $bytes=[IO.File]::ReadAllBytes($path)
    $sha=[Security.Cryptography.SHA256]::Create()
    try { $hash=([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
    return @{ Data=([Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json); Hash=$hash }
}
$proposed=Read-Proof (Join-Path $EvidenceRoot 'compatibility-audit\registry-proposed-map.json')
$discovery=Read-Proof (Join-Path $EvidenceRoot 'compatibility-audit\registry-stage2-evidence.json')
$prior=Read-Proof (Join-Path $EvidenceRoot 'flag-map\current-reviewed-map-descriptor.json')
$anomaly=Read-Proof (Join-Path $EvidenceRoot 'compatibility-audit\registry-anomaly-evidence.json')
if (!$proposed.Data.RegistryTraversalValidated -or !$discovery.Data.traversal_validated -or $proposed.Data.Entries.Count -ne 160) { throw 'Expected the reviewed160-entry registry map.' }
$artifactId='registry-stage2:'+ $discovery.Hash
$old=@{}; foreach ($entry in $prior.Data.Entries) { $old.Add($entry.Name,$entry) }
$groups=@(); $groupIds=@{}
foreach ($group in $proposed.Data.CalibrationGroups) {
    if (!$group.unambiguous_multiple_calibration -or $group.calibrators.Count -lt 2 -or !$group.rtti.validated) { throw 'Unproven calibration group.' }
    $id=$group.type+':'+$group.rtti.vtable_rva+':'+$group.rtti.type_descriptor_rva
    $groupIds.Add($group.rtti.decorated_type,$id)
    $calibrators=@($group.calibrators | ForEach-Object {
        if ($_.type -ne $group.type -or $_.width -ne $group.width) { throw 'Contradictory calibrated width.' }
        [ordered]@{ Name=$_.name; Type=$_.type; Width=$_.width; Rva=$_.scalar_rva; ConsumerEvidence=$_.consumer }
    })
    $groups += [ordered]@{ Id=$id; Approved=$true; Type=$group.type; Width=$group.width; Rtti=$group.rtti; Calibrators=$calibrators }
}
$entries=@()
foreach ($entry in $proposed.Data.Entries) {
    $p=$entry.Provenance
    $row=@($discovery.Data.flags | Where-Object name -eq $entry.Name)
    if ($row.Count -ne 1 -or $row[0].status -ne 'registry_type_calibrated_module_anchor_verified' -or $row[0].matches.Count -ne 1) { throw ('Missing exact registry chain: '+$entry.Name) }
    $match=$row[0].matches[0]
    if (!$match.module_anchor_validated -or !$match.storage_in_pinned_module -or $match.scalar_rva -ne $entry.Rva -or !$match.rtti.validated -or $match.rtti.decorated_type -ne $p.Rtti.decorated_type) { throw ('Unproven scalar chain: '+$entry.Name) }
    $width=if($entry.Type -eq 'Boolean'){1}elseif($entry.Type -eq 'Int32'){4}else{throw 'Unsupported type.'}
    $nameBytes=[Text.Encoding]::ASCII.GetBytes($p.RegistryKey+[char]0)
    if ($nameBytes.Length -ne $p.ExpectedNameLength+1) { throw 'Name length disagreement.' }
    $consumer=$null
    if ($old.ContainsKey($entry.Name)) {
        $o=$old[$entry.Name]
        if ($o.Rva -ne $entry.Rva -or $o.Type -ne $entry.Type -or $o.Width -ne $width) { throw ('Prior evidence conflict: '+$entry.Name) }
        $consumer=$o.ConsumerEvidence
    }
    $entries += [ordered]@{
        Name=$entry.Name; Rva=$entry.Rva; Type=$entry.Type; Width=$width; ByteOrder='little-endian'; RuntimeEffectProven=$false
        EvidenceKind=$(if($consumer){'ConsumerInstructionAndCalibratedRegistryScalar'}else{'CalibratedRegistryScalar'})
        StaticEvidence=[ordered]@{ NameInImage=$p.RegistryKey; NameStringRva=$p.NameStringRva; NamePointerRva=$p.NamePointerRva; NameLengthRva=$p.NameLengthRva; ExpectedNameLength=$p.ExpectedNameLength; ExpectedNameBytesHex=([BitConverter]::ToString($nameBytes)).Replace('-','').ToLowerInvariant() }
        RegistryEvidence=[ordered]@{ Approved=$true; ArtifactId=$artifactId; RegistryKey=$p.RegistryKey; ScalarRva=$entry.Rva; KeyDescriptorScalarChainVerified=$true; ModuleAnchorsValidated=$true; CalibrationId=$groupIds[$p.Rtti.decorated_type]; CalibrationNames=@($p.CalibrationNames); PointerSlotRva=$p.PointerSlotRva; NodeDescriptorOffset=$p.NodeDescriptorOffset; DescriptorStoragePointerOffset=$p.DescriptorStoragePointerOffset }
        ConsumerEvidence=$consumer
        ConstraintNote=$(if($entry.Name -eq 'DFIntS2PhysicsSenderRate'){'The reviewed engine consumer limits this value to at most240. The requested profile value is preserved.'}else{''})
    }
}
foreach($name in @('FFlagNextGenReplicatorEnabledRead','FFlagNextGenReplicatorEnabledWrite')) {
    $o=$old[$name]; $fresh=@($anomaly.Data.flags | Where-Object name -eq $name)
    if (!$o -or $fresh.Count -ne 1 -or !$fresh[0].module_anchor_validated -or $fresh[0].scalar_rva -ne $o.Rva) { throw ('Missing refreshed consumer-only proof: '+$name) }
    $instructions=@($fresh[0].consumers | ForEach-Object { $_.instructions } | Where-Object { $_.rva -eq $o.ConsumerEvidence.Rva -and $_.bytes -eq $o.ConsumerEvidence.ExpectedBytesHex -and $_.exact_scalar_read_widths -contains 1 })
    if ($instructions.Count -eq 0) { throw ('Fresh instruction does not match: '+$name) }
    $entries += [ordered]@{ Name=$name; Rva=$o.Rva; Type=$o.Type; Width=$o.Width; ByteOrder='little-endian'; RuntimeEffectProven=$false; EvidenceKind='ConsumerInstruction'; StaticEvidence=$o.StaticEvidence; ConsumerEvidence=$o.ConsumerEvidence; ConsumerReviewArtifactId=('registry-anomaly:'+ $anomaly.Hash); RegistryEvidence=$null }
}
$names=@{}; $end=0
foreach($entry in @($entries | Sort-Object { [uint64]$_.Rva })) {
    if($names.ContainsKey($entry.Name) -or [uint64]$entry.Rva -lt $end -or [uint64]$entry.Rva+[uint64]$entry.Width -gt [uint64]$proposed.Data.Executable.ImageSize) { throw ('Duplicate, overlapping or out-of-range scalar: '+$entry.Name+' RVA='+$entry.Rva+' previousEnd='+$end) }
    if($entry.Width -eq 4 -and $entry.Rva % 4 -ne 0){throw 'Unaligned Int32.'}
    $names.Add($entry.Name,$true); $end=[uint64]$entry.Rva+[uint64]$entry.Width
}
$excluded=@()
foreach($flag in $discovery.Data.flags) {
    if($names.ContainsKey($flag.name)){continue}
    switch($flag.status) {
        'selected_key_not_found' {$code='RegistryKeyNotFound';$reason='The selected key was not found in the current registry; no supported live scalar mapping.'}
        'unregistered_descriptor_no_calibrated_storage' {$code='UnregisteredDescriptor';$reason='Internal UnregisteredValueGetSet descriptor has no calibrated module scalar; it is not treated as heap scalar storage.'}
        'descriptor_found_storage_not_module' {$code='UnregisteredDescriptor';$reason='No calibrated module scalar is available for this descriptor.'}
        'conflicting_prior_evidence_requires_resolution' {$code='WidthConflict';$reason='Conflicting prior width evidence remains unresolved; no live write is permitted.'}
        'module_storage_anchor_unresolved' {
            $type=$flag.matches[0].rtti.decorated_type
            if($type -like '*basic_string*'){$code='UnsupportedString';$reason='Internal string storage is unsupported; the profile value remains unchanged.'}
            elseif($type -like '*Channel*'){$code='UnsupportedChannel';$reason='Internal FLog Channel storage is not a calibrated Int32; live writing is unsupported.'}
            else{throw 'Unexpected unresolved internal type.'}
        }
        default {throw ('Unexpected exclusion status: '+$flag.status)}
    }
    $excluded += [ordered]@{Name=$flag.name;Code=$code;Reason=$reason}
}
$executable=[ordered]@{Version=$proposed.Data.Executable.Version;Sha256=$proposed.Data.Executable.Sha256;FileSize=$proposed.Data.Executable.FileSize;ImageSize=$proposed.Data.Executable.ImageSize}
$map=[ordered]@{SchemaVersion=2;Executable=$executable;Review=[ordered]@{ArtifactId=$artifactId;RegistryMapSha256=$proposed.Hash;RegistryEvidenceSha256=$discovery.Hash;PriorConsumerMapSha256=$prior.Hash;ConsumerAnomalySha256=$anomaly.Hash};RuntimeEffectProven=$false;CoreEntries=@($entries|ForEach-Object{[ordered]@{Name=$_.Name;Rva=$_.Rva;Type=$_.Type}});Entries=$entries;CalibrationGroups=$groups;IneligibleEntries=$excluded}
if($entries.Count -ne 162 -or $excluded.Count -ne 50){throw 'Reviewed final coverage must be162 eligible and50 excluded.'}
$output=Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Nstrap.App\RuntimeMaps\ReviewedMap.json'
[IO.File]::WriteAllText($output,($map|ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))
Write-Output ('Generated162 reviewed module scalars and50 reasoned exclusions: '+$output)
