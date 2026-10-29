# Tunapp

Android en .NET MAUI 10, con conexión directa a Firebase Authentication y Cloud Firestore (`tunapp-j27`). No requiere servidor ASP.NET Core ni URL de API.

## Estado de esta fase

- Inicio con bienvenida, acciones de cosecha/tratamiento y tarjetas cuadradas para fincas, cooperativas, campañas, historial, equipo y cuenta.
- Listados de gestión con buscador sin tildes ni distinción de mayúsculas, botón Añadir y tarjetas compactas. Detalles y formularios se abren en páginas independientes; la búsqueda se conserva al volver. Añadir y las acciones de administración respetan los permisos.
- Formularios independientes para cosechas y tratamientos, cooperativas e historial filtrado por campaña. Guardado local y cola Firebase con las mismas garantías de permisos y aislamiento.
- Registro, inicio de sesión, recuperación de contraseña y verificación de correo mediante Firebase.
- Explotaciones compartidas: propietario, administradores, operarios y lectores. Invitaciones dirigidas a un correo verificado; cambio de permisos y retirada de acceso.
- Campañas con fechas opcionales, selección, histórico y cierre/reapertura administrativa.
- Fincas con ficha común a todas las campañas, sin duplicados. Los datos anuales antiguos se conservan almacenados; cosechas y tratamientos siguen separados por campaña.
- SQLite por cuenta: datos y cola se guardan juntos en una transacción antes de intentar el envío. Consulta y edición sin cobertura después del primer acceso.
- Sincronización al abrir la pantalla, recuperar conexión, guardar, pulsar Sincronizar y periódicamente mientras la pantalla permanece visible. Los cambios pendientes sobreviven al cierre del proceso.
- Recibos de operaciones para reconocer un guardado cuya respuesta se perdió. Precondiciones con `updateTime` para impedir sobrescrituras silenciosas entre móviles.
- Pantalla Sincronización: pendientes, errores y comparación explícita de conflictos. Elegir datos del móvil o datos de la nube requiere confirmación.

**Versión anterior de las reglas publicada en `tunapp-j27` el 27/10/2026 con autorización del propietario.** Versión: `00ada075-3726-408d-b550-85d4ffca8ee5`. Las reglas nuevas para campos opcionales, cooperativas, cosechas y tratamientos están verificadas en emuladores y pendientes de publicación. Las verificaciones anteriores contra Firebase real no cubren estas funciones nuevas.

No se ha activado facturación, contratado servicios ni publicado la app. La API anterior se conserva fuera de la solución principal como referencia; la app ya no la utiliza. La clave privada de servicio permanece fuera del proyecto y no es necesaria para ejecutar la aplicación.

## Uso

1. Entrar o crear una cuenta desde Inicio. Los datos antiguos del dispositivo siguen accesibles desde la entrada local o desde Mi cuenta, sin mezclarlos con la cuenta Firebase.
2. Crear una explotación y su primera campaña. Todas las fincas aparecen en cada campaña; no se copian movimientos.
3. Abrir Fincas y usar Añadir junto al buscador. La ficha es común a todas las campañas y la superficie es opcional. Al tocar una tarjeta se abre su detalle; Editar abre un formulario independiente.
4. El administrador crea una invitación desde Usuarios. El familiar entra con ese correo, lo verifica, sincroniza y acepta la invitación dentro de Tunapp. No se envía un correo de invitación automáticamente.
5. Revisar los cambios pendientes desde Mi cuenta → Sincronización. Guardado en el móvil y confirmado en Firebase son estados diferentes.

Las invitaciones y la gestión de permisos necesitan conexión. El primer registro/inicio de sesión también. Sin cobertura se puede utilizar la información descargada de esa cuenta y registrar cambios agrícolas. Una campaña nueva debe sincronizarse antes de cerrarla.

Las retiradas de acceso se aplican al comprobarlas con Firebase: se retira de la vista la explotación revocada y se conservan bloqueados los cambios que aún no se habían enviado. Un móvil sin cobertura no puede conocer inmediatamente una revocación remota.

## Datos anteriores

La pestaña Datos anteriores conserva `tunapp-v1.json`, su copia `.bak` y todas las funciones locales previas. No se ha borrado ni subido automáticamente ningún dato anterior. Su importación a una cuenta Firebase se hará de forma explícita para no mezclar explotaciones ni duplicar registros.

Los datos nuevos se guardan en `FileSystem.AppDataDirectory/accounts/cloud-{SHA256(uid)}.db3`. SQLite usa WAL y sincronización FULL. Cada archivo verifica la cuenta propietaria al abrirse. Los tokens se guardan en SecureStorage y la contraseña no se persiste. Los archivos SQLite no están cifrados por esta aplicación; permanecen en su almacenamiento privado del sistema operativo. No borrar/reinstalar la app con cambios pendientes sin una copia de seguridad: aún falta la exportación recuperable de estos datos.

## Estructura

- `src/Tunapp.Presentation`: ViewModels independientes de MAUI con `CommunityToolkit.Mvvm` 8.4.0. `ScreenViewModel`, `FieldViewModel`, formularios y listados usan propiedades `[ObservableProperty]` y comandos `[RelayCommand]`/`AsyncRelayCommand`. Incluye acceso, gestión, cosechas, tratamientos y compatibilidad local.
- `ScreenPage.cs` y `DashboardView.cs`: vistas C# que crean controles y enlazan propiedades/comandos; sin acceso directo a Firebase ni lógica de negocio. `MainPage.xaml.cs` solo inicializa la vista.
- `CloudPage.cs`: vista de inicio; reenvía el ciclo de vida, el temporizador y los cambios de conectividad al ViewModel.
- `Services`: adaptadores MAUI de navegación, conectividad y `SecureStorage`, detrás de `IAppNavigation` e `IAppEnvironment`.
- `MauiProgram.cs`: registro por inyección de dependencias de navegación, entorno, `HttpClient`, autenticación, servicios locales y ViewModels de entrada. Los ViewModels de gestión reciben el contexto de la cuenta/explotación al navegar.
- `tests/Tunapp.MvvmChecks`: comprobaciones sin dispositivo ni Firebase real de comandos, errores, reintentos, permisos, campos opcionales y separación de datos. Ejecutar: `dotnet run --project tests/Tunapp.MvvmChecks`.

La interfaz conserva vistas construidas en C#; MVVM no exige convertirlas a XAML. Los ViewModels no referencian controles, páginas ni el SDK MAUI. Las reglas y la persistencia permanecen en `Tunapp.Core` y `firestore.rules`.

- `src/Tunapp.Core/FirestoreRest.cs`: REST oficial con token de usuario, paginación y escrituras atómicas con precondiciones.
- `src/Tunapp.Core/CloudDatabase.cs`: caché y cola SQLite por cuenta, confirmación y resolución de conflictos.
- `src/Tunapp.Core/DirectFarmService.cs`: validación local, persistencia primero, sincronización y gestión de usuarios.
- `src/Tunapp.Core/FirebaseAuthClient.cs`: autenticación, renovación de tokens y verificación de correo.
- `firestore.rules`: aislamiento y validación en Firebase, independientemente de lo que envíe el móvil.
- `tests/firebase`: pruebas de reglas mediante la biblioteca oficial y el emulador.
- `tests/Tunapp.DirectChecks`: cliente C# real y SQLite real contra emuladores, con fallos de red inducidos.
- `tests/Tunapp.Checks`: comprobaciones de la fase local anterior.
- `src/Tunapp.Api` y `scripts/Start-Api.ps1`: implementación anterior archivada; no hacen falta para esta arquitectura y no forman parte de la solución principal.
- `tests/Tunapp.CloudChecks`: conserva las comprobaciones anteriores y añade `--live --direct` para probar la conexión directa real. Solo usa Admin para verificar correos temporales y limpiar los recursos de prueba; todas las operaciones verificadas pasan por las reglas con tokens de usuario.

No se necesita `google-services.json` para esta implementación REST. La app solo incluye la configuración pública Firebase, nunca credenciales Admin ni una biblioteca de servidor Firestore.

## Modelo en Firestore y permisos

- `farms/{farmId}`: explotación, propietario y metadatos.
- Subcolecciones `members`, `campaigns`, `plots`, `campaignPlots` y `operations` por explotación.
- `users/{uid}/farms/{farmId}`: índice autorizado de explotaciones.
- `invitations/{correoNormalizado}/farms/{farmId}`: invitación que solo puede aceptar ese correo verificado.

Las reglas exigen crear explotación, propietario e índice en la misma operación atómica. El propietario siempre conserva el acceso de administrador. Añadir o retirar membresías exige mantener su índice en el mismo lote. Aceptar una invitación consume esa invitación para impedir reutilizarla después de una retirada de acceso.

Las reglas validan tipos, campos permitidos, permisos, fechas, superficie y referencias dentro de la misma explotación. Las actuaciones anuales solo se escriben si la campaña permanece abierta al confirmar el lote. Se usan campos nativos de Firestore, sin JSON opaco que las reglas no puedan validar. La superficie sin indicar se guarda como null. Las superficies indicadas se guardan como metros cuadrados enteros; se admiten hasta cuatro decimales de hectárea, sin redondeo silencioso. Las fechas de campaña son fechas UTC sin componente horario funcional.

Los recibos de sincronización registran actor, destino, huella del cambio y fecha del servidor; no conceden permisos. No hay borrado de campañas o fincas desde clientes. Las colecciones de la API anterior (`businesses`) permanecen cerradas a clientes y no se migran ni eliminan automáticamente.

Los listados actuales descargan todas las campañas y fincas de cada explotación autorizada, paginando las peticiones. Antes de añadir grandes volúmenes de movimientos se incorporarán descarga por campaña, cursores y sincronización incremental para limitar lecturas.

## Compilar y probar

```powershell
dotnet build Tunapp.csproj -f net10.0-android -p:EmbedAssembliesIntoApk=true
npm.cmd ci --prefix tests/firebase
.\scripts\Start-FirebaseEmulators.ps1
```

En otra terminal, ejecutar SECUENCIALMENTE (las pruebas de reglas vacían únicamente el proyecto ficticio del emulador):

```powershell
node --test tests/firebase/rules.test.mjs
dotnet run --project tests/Tunapp.DirectChecks/Tunapp.DirectChecks.csproj
dotnet run --project tests/Tunapp.Checks/Tunapp.Checks.csproj
```

Los emuladores usan el proyecto ficticio fijo `demo-tunapp-direct`; nunca las credenciales ni los datos reales de `tunapp-j27`. El script localiza Java 21 en la instalación Android existente; puede recibir otra ubicación mediante `-JavaHome`.

Verificado:

- 22 pruebas de reglas correctas en el emulador oficial, incluyendo acceso anónimo, usuarios ajenos, lectores, operarios, campañas cerradas, invitaciones, propietario y retirada de acceso.
- 27 comprobaciones directas correctas con el cliente C# y SQLite en Windows: reinicio offline, renovación de sesión al reconectar, dos almacenes de dispositivo, respuesta perdida, ausencia de duplicados, conflictos, cierre remoto y revocación aun si otra descarga falla después.
- Las 21 comprobaciones locales anteriores siguen pasando.
- APK Android Debug autocontenido compilado con 0 advertencias y 0 errores: `bin/Debug/net10.0-android/com.companyname.tunapp-Signed.apk`.
- Instalación y arranque del APK comprobados en un emulador Android API 30. Pantalla de acceso revisada visualmente y con jerarquía nativa: [captura](artifacts/qa/login-android.png). No solicita una URL de API.
- El intento de registro desde Android terminó en el aviso de conexión no confirmada. No se ha validado un registro ni SQLite desde la interfaz Android ni una sincronización contra Firestore real en esta fase. El ensayo se limpió y se detuvo el emulador de prueba.
- Inspección del APK: sin archivos de cuenta de servicio, bibliotecas Firebase Admin/Firestore de servidor ni herramientas Node de prueba.
- Tras publicar las reglas: 14 comprobaciones correctas contra Firebase REAL, usando el cliente C# de la app: guardado y descarga desde otra caché, invitaciones verificadas, lector, operario, rechazo de elevación de permisos, campaña cerrada, reapertura y revocación de acceso. Se eliminaron todas las cuentas y documentos temporales al terminar. No se utilizó nuestra API ASP.NET Core.

Las pruebas locales y de emulador se distinguen de estas 14 comprobaciones reales. Ninguna sustituye la prueba de la interfaz y la conexión desde vuestros teléfonos.

## Reglas publicadas y futuras actualizaciones

El archivo exacto y revisable es `firestore.rules`, asociado al proyecto `tunapp-j27` mediante `.firebaserc`. La versión anterior se conserva en `artifacts/deploy/firestore-before-1793114379453.json`. No activar modo de prueba ni permitir acceso público como alternativa.

La publicación se realizó mediante el SDK oficial, exclusivamente sobre las reglas, sin ampliar permisos IAM ni modificar índices. El script conserva una copia de la versión anterior y verifica la fuente después de publicar. Para futuras actualizaciones autorizadas:

```powershell
$env:GOOGLE_APPLICATION_CREDENTIALS='C:\ruta\privada\credencial.json'
node scripts/Deploy-FirestoreRules.cjs --publish
```

La publicación cambia la política de acceso y requiere autorización. Para repetir expresamente las comprobaciones contra Firebase real (crean y eliminan sus propios recursos temporales):

```powershell
$env:TUNAPP_FIREBASE_API_KEY='clave-publica-Firebase'
dotnet run --project tests/Tunapp.CloudChecks/Tunapp.CloudChecks.csproj -- --live --direct
```

## Siguientes fases

1. Probar instalación, reconexión y acceso familiar en vuestros teléfonos. La publicación de reglas y la comprobación directa real ya están completadas.
2. Exportación/recuperación de copias locales, importación explícita de datos anteriores y descarga incremental por campaña.
3. Tareas y tratamientos, cosechas y entregas con foto y revisión OCR, gastos e ingresos; campaña obligatoria para todos los movimientos y reglas correspondientes antes de habilitarlos.
4. Comparación de campañas, mapas, límites, accesos y recorridos GPS. Después, filas, anchura, giros y GNSS/RTK. Tractor conducido manualmente.

Antes de añadir fotos en Cloud Storage, OCR, mapas u otro servicio con facturación, revisar y explicar sus costes. La conexión directa no requiere un servidor propio.

Referencias: [Firestore REST y autenticación](https://firebase.google.com/docs/firestore/use-rest-api), [escrituras atómicas](https://firebase.google.com/docs/firestore/manage-data/transactions), [reglas y emulador](https://firebase.google.com/docs/firestore/security/test-rules-emulator), [SQLite en MAUI](https://learn.microsoft.com/en-us/dotnet/maui/data-cloud/database-sqlite).

Los cambios de fechas y superficie opcionales requieren publicar la nueva versión de `firestore.rules` antes de usarlos contra Firebase real. Validar la interfaz en un dispositivo antes de distribuir el APK. Cosechas y tratamientos básicos implementados; sincronización validada en emuladores.

## Cosechas: cooperativa y calidad

- El formulario siempre muestra la cooperativa y permite crearla sin salir del registro de cosecha. La cooperativa es opcional.
- Calidad obligatoria en el formulario: Árbol o Suelo. Los registros anteriores conservan su calidad sin indicar.
- Cooperativas muestra los kilos entregados en la campaña seleccionada; su detalle desglosa Árbol, Suelo y sin calidad indicada.
- El historial muestra y permite buscar cooperativa y calidad.
- Verificación: 48 comprobaciones locales, 26 pruebas de reglas y 36 comprobaciones directas con SQLite y emuladores correctas. Incluye persistencia de calidad entre dispositivos y aislamiento de totales.
- Las reglas actualizadas de Firebase siguen pendientes de publicación. No se ha verificado visualmente este formulario en dispositivo.
