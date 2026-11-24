$ErrorActionPreference='Stop'
$asm = [Reflection.Assembly]::ReflectionOnlyLoadFrom($args[0])
'ASSEMBLY '+$asm.FullName
'RUNTIME '+$asm.ImageRuntimeVersion
foreach ($ref in $asm.GetReferencedAssemblies()) { 'REFERENCE '+$ref.FullName }
foreach ($attr in $asm.GetCustomAttributesData()) { 'ATTRIBUTE '+$attr.ToString() }
foreach ($type in $asm.GetExportedTypes()) {
  'TYPE '+$type.FullName+' base='+$type.BaseType+' interfaces='+(@($type.GetInterfaces() | ForEach-Object { $_.FullName }) -join ',')
  foreach ($ctor in $type.GetConstructors()) { ' CTOR '+$ctor.ToString() }
  foreach ($field in $type.GetFields([Reflection.BindingFlags]'Public,Instance,Static,DeclaredOnly')) { ' FIELD '+$field.FieldType.FullName+' '+$field.Name+' '+$(if($field.IsLiteral){$field.GetRawConstantValue()}) }
  foreach ($property in $type.GetProperties([Reflection.BindingFlags]'Public,Instance,Static,DeclaredOnly')) { ' PROPERTY '+$property.ToString() }
  foreach ($method in $type.GetMethods([Reflection.BindingFlags]'Public,Instance,Static,DeclaredOnly')) {
    ' METHOD '+$method.ToString()+' static='+$method.IsStatic
    foreach ($parameter in $method.GetParameters()) { '  PARAM '+$parameter.Name+' '+$parameter.ParameterType.FullName+' out='+$parameter.IsOut+' optional='+$parameter.IsOptional }
  }
}



