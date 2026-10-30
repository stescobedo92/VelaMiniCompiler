# Revisión de seguridad y semántica — 2026-10-29

Revisión directa del código y pruebas locales sobre la revisión base
`c4b83a9e32637a2af7dac5c188bb46ed3bdfc53e`. Incluye revisión cruzada de
subagentes y correcciones destinadas a la versión **0.4.0**.

La severidad describe el impacto previo a la corrección y depende de cómo se
expongan las entradas. No equivale a una vulnerabilidad remota demostrada.

## Fallos corregidos

| ID | Severidad | Fallo y condición | Corrección y evidencia de regresión |
| --- | --- | --- | --- |
| S01 | Media | `Array<Item>(1)[0]` y estructuras con referencias devolvían valores no inicializados como tipos no nulos; el acceso posterior producía una excepción CLR ajena a `catch VelaRuntimeException`. | [VelaArray](https://github.com/stescobedo92/VelaMiniCompiler/blob/v0.4.0/src/Vela.Runtime/VelaArray.cs) verifica lectura, enumeración y copia; registra inicialización de estructuras con referencias. Conserva valores predeterminados numéricos, `None` y `Any = nil`. Pruebas de runtime y ejecución de código Vela generado. |
| S02 | Media | `Option<T> == nil`, `Option<T> == Option<T>`, igualdad de `Result` y de estructuras pasaban `check` pero fallaban al compilar C#. | [Emisor](https://github.com/stescobedo92/VelaMiniCompiler/blob/v0.4.0/src/Vela.Backend/CSharpEmitter.cs) comprueba presencia para `nil` y usa comparación de valores para los tipos que la necesitan. Evalúa cada operando una vez; preserva identidad de `Any`. Pruebas con ejecución, nombre `EqualityComparer` definido por el usuario y comparaciones inválidas. |
| S03 | Baja | Un `catch` válido que no consultaba su variable producía CS0168 con warnings como errores. | [Emisor de sentencias](https://github.com/stescobedo92/VelaMiniCompiler/blob/v0.4.0/src/Vela.Backend/CSharpEmitter.Statements.cs) suprime únicamente CS0168 alrededor de la declaración generada. La prueba S01 compila y ejecuta handlers sin uso de la variable. |
| S04 | Media | `json.try_get_int` recibía JSON válido con propiedad `null`, texto o booleano y lanzaba `InvalidOperationException`. | [VelaJson](https://github.com/stescobedo92/VelaMiniCompiler/blob/v0.4.0/src/Vela.Runtime/VelaCoreModules.cs) verifica el tipo Number; devuelve `None` para tipos o rangos incompatibles. Pruebas parametrizadas. |
| S05 | Media | Ejecutar consultas después de `Close()` podía acceder a `_connection = null` sin que el indicador de disposición lo detectara. | [SQLite](https://github.com/stescobedo92/VelaMiniCompiler/blob/v0.4.0/src/Vela.Sqlite.Runtime/VelaSqlite.cs) y [PostgreSQL](https://github.com/stescobedo92/VelaMiniCompiler/blob/v0.4.0/src/Vela.Postgres.Runtime/VelaPostgres.cs) unifican cierre y disposición; generan `VelaIoException` controlada. SQLite verificado en memoria; PostgreSQL compila, con integración condicionada a configuración externa. |
| S06 | Media | Liberar un task handle obsoleto o de otro propietario cancelaba primero la tarea activa del mismo slot y sólo después rechazaba el handle. | [VelaTaskHandleTable](https://github.com/stescobedo92/VelaMiniCompiler/blob/v0.4.0/src/Vela.Runtime/Interop/VelaTaskHandleTable.cs) valida identidad completa antes de efectos y conserva el token hasta terminar el trabajo. Regresiones de propietario, tipo y generación inválidos. No se demostró exposición remota. |
| S07 | Media | Si `Dispose` de un valor almacenado lanzaba, el slot quedaba activo con contador cero; la siguiente liberación podía desbordar. | [VelaHandleTable](https://github.com/stescobedo92/VelaMiniCompiler/blob/v0.4.0/src/Vela.Runtime/Interop/VelaHandleTable.cs) invalida antes de limpieza externa, informa el error y continúa limpiando los demás recursos. Pruebas de cleanup síncrono y asíncrono fallido. |
| S08 | Media | Mensajes LSP sin ID requerido, campos de tipo incorrecto o framing inválido provocaban excepciones no controladas. | [LspServer](https://github.com/stescobedo92/VelaMiniCompiler/blob/v0.4.0/src/Vela.LanguageServer/LspServer.cs) valida envelope, parámetros, texto JSON y framing; devuelve errores JSON-RPC o cierra limpiamente una trama irrecuperable. Límites de 16 MiB por cuerpo y 8 KiB de headers. La superficie comprobada es stdio local. |
| S09 | Baja | Argumentos GraphQL con comas dentro de strings generaban JSON inválido; ciertas respuestas del resolver se aceptaban como JSON por su primer carácter. | [VelaGraphql](https://github.com/stescobedo92/VelaMiniCompiler/blob/v0.4.0/src/Vela.Http.Runtime/VelaGraphql.cs) analiza el subconjunto escalar documentado, consume el documento completo antes del resolver y serializa la respuesta. Regresiones de comas, escapes, Unicode y sintaxis no soportada. |
| S10 | Alta | Restaurar nombre/versión desde un registro B reutilizaba archivos previamente descargados de A porque el caché omitía el origen. | [VelaPackageCache](https://github.com/stescobedo92/VelaMiniCompiler/blob/v0.4.0/src/Vela.Packages/VelaPackageCache.cs) y [restore](https://github.com/stescobedo92/VelaMiniCompiler/blob/v0.4.0/src/Vela.Packages/VelaRestoreClient.cs) aíslan el caché por URI de registro. Pruebas con dos registros file y dos HTTP simulados que publican contenido distinto. |
| S11 | Alta, condicionada a TUF | Un HTTP 500/403 al pedir metadatos se trataba como ausencia de firmas y permitía completar la restauración. | [Restore](https://github.com/stescobedo92/VelaMiniCompiler/blob/v0.4.0/src/Vela.Packages/VelaRestoreClient.cs) sólo acepta ausencia si ambos endpoints devuelven 404. Otros errores y metadatos parciales abortan sin guardar el paquete. Pruebas HTTP simuladas y file. |
| S12 | Media | `signed: null`, campos requeridos ausentes o elementos nulos en colecciones TUF llegaban a dereferencias internas. | [TufVerifier](https://github.com/stescobedo92/VelaMiniCompiler/blob/v0.4.0/src/Vela.Packages/Tuf/TufVerifier.cs) y [contexto JSON](https://github.com/stescobedo92/VelaMiniCompiler/blob/v0.4.0/src/Vela.Packages/Tuf/TufJsonContext.cs) validan nulabilidad, campos requeridos y colecciones; generan `TufVerificationException`. Regresiones de entradas malformadas. |
| S13 | Media | HTTP trataba `^1.0.0` y `~1.0.0` como la versión exacta base si existía, en lugar de elegir la mayor versión compatible como el registro file. | [Restore](https://github.com/stescobedo92/VelaMiniCompiler/blob/v0.4.0/src/Vela.Packages/VelaRestoreClient.cs) reserva la ruta rápida para versiones exactas y resuelve rangos desde el índice. Regresiones de paridad file/HTTP. |
| S14 | Alta | El servidor de registro permitía publicar sin autenticar y sobrescribir versiones existentes. | Política de publicación con clave de administrador y HTTPS, modo de sólo lectura sin clave e instalación atómica que devuelve 409 para versiones existentes. Pruebas HTTP reales sobre Kestrel. |
| S15 | Alta | La raíz descargada se validaba a sí misma, permitiendo sustituir la identidad de un registro. | Raíz inicial obtenida por un canal independiente mediante `--trusted-root`, estado persistido por registro y rotación secuencial con umbral de firmas de ambas raíces. Se rechazan sustituciones y saltos de versión. |
| S16 | Alta | No existía estado persistente para detectar rollback de metadatos ni para impedir degradar un registro confiable a paquetes sin firma. | Persistencia atómica bajo bloqueo entre procesos, versiones y digest de targets monotónicos, expiración y política de fallo cerrado. `--allow-unsigned` requiere ausencia completa de metadatos y nunca sustituye confianza establecida. |
| S17 | Alta | Los paquetes ya extraídos se reutilizaban sin detectar alteraciones del caché local. | Cada restauración firmada valida el archivo contra los targets actuales y reconstruye el contenido; vuelve a descargar archivos corruptos. Se comprueba la identidad del manifiesto. |
| S18 | Alta | Archivos comprimidos podían consumir recursos desproporcionados o utilizar rutas ambiguas y enlaces. | Límites de bytes comprimidos/descomprimidos y entradas, validación previa de rutas, colisiones, enlaces y archivos especiales, con control del tamaño real durante extracción. Publicación limitada también por concurrencia. |
| S19 | Media | La canonicalización TUF truncaba caracteres Unicode; índices JSON con tipos inesperados podían provocar excepciones sin traducir. | UTF-8 estricto, rechazo de claves JSON duplicadas y validación de esquemas; errores de restauración controlados. Límites de metadatos, rotaciones y duración total. |
| S20 | Baja | Una prueba del formateador dependía de los saltos de línea del checkout y fallaba en Windows. | Regresión parametrizada con LF y CRLF que comprueba el resultado canónico LF y su idempotencia. |
| S21 | Baja | La prueba de integración de `--check-file` reconstruía el servidor internamente y leía secuencialmente stdout y stderr, sin límite de tiempo; podía bloquear la suite. | Ejecuta el binario ya construido, consume ambas salidas concurrentemente y termina el proceso si excede el tiempo límite. Conserva la prueba de integración real. |

## Alcance y límites

- El soporte de metadatos implementa raíces y targets. No implementa los roles
  timestamp, snapshot ni delegaciones de TUF; no se presenta como TUF completo.
  Eliminar el estado de confianza local elimina su memoria contra rollback.
- La publicación utiliza un administrador con clave configurada, no un modelo
  multiusuario. El directorio del registro y el caché deben tener permisos del
  sistema operativo que impidan su modificación por usuarios no confiables.
- El registro de dependencias `.vlpkg` y la galería de herramientas `.vpkg` son
  protocolos distintos. Véase la [guía del registro](registry-security.md).
- No se certifica ausencia de otros defectos. No hubo explotación de sistemas
  externos, pruebas de carga ni auditoría completa de dependencias.

## Referencias técnicas

Consultadas mediante Context7 como referencia para las correcciones:

- [.NET: seguridad de nulabilidad y arrays](https://learn.microsoft.com/dotnet/csharp/nullable-references).
- [System.Text.Json: nulabilidad](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/nullable-annotations).
- [System.Text.Json: propiedades requeridas](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/required-properties).
- [TUF: especificación de confianza inicial y rotación](https://github.com/theupdateframework/specification/blob/master/tuf-spec.md).
- [ASP.NET Core: limitación de peticiones](https://learn.microsoft.com/aspnet/core/performance/rate-limit).

Las reproducciones se ejecutaron localmente con datos de prueba; las regresiones
permanentes están en las suites Backend, Runtime, Packages, LanguageServer, HTTP,
SQLite, PostgreSQL, CLI y Registry.Server.

## Verificación

Ejecutada con .NET SDK 10.0.403 y runtime 10.0.14:

```text
dotnet build Vela.slnx --configuration Release --verbosity quiet -m:1
dotnet test Vela.slnx --configuration Release --no-build --no-restore --verbosity minimal -m:1 --blame-hang-timeout 90s --blame-hang-dump-type none
```

- Compilación Release completa: **0 errores y 0 advertencias**.
- Suite completa: **516 aprobadas, 0 fallidas y 1 omitida**, en 12 proyectos.
  Backend 86; CLI 12; Core 36; gRPC 1; HTTP 53; LanguageServer 72;
  Packages 151; PostgreSQL 3; Registry.Server 28; Runtime 62; SQLite 4; UI 8.
- NativeAOT `win-x64`: publicación Release completada; ejecutable comprobado con
  análisis de código Vela y restauración firmada inicial y con confianza persistida.
- Documentación: `mint broken-links` sin enlaces rotos; generadas las 20 páginas
  del sitio para 0.4.0.
- La omitida es la prueba HTTPS externa existente. El smoke test PostgreSQL
  retorna sin abrir conexión cuando falta `POSTGRES_TEST_CONNECTION_STRING`;
  su resultado aprobado no representa una integración real con PostgreSQL.
- Se usó el SDK directamente y se separó build de test. El bloqueo detectado en
  la integración de LSP se corrigió en S21; ninguna prueba se desactivó.
- Se revisaron los diffs con revisión cruzada y `git diff --check`.
