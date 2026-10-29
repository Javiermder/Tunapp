# Tunapp | Cuaderno de Campo Digital & Gestión Agrícola

[![.NET MAUI](https://img.shields.io/badge/.NET%20MAUI-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![C#](https://img.shields.io/badge/C%23-12.0-239120?logo=csharp)](https://docs.microsoft.com/dotnet/csharp/)
[![SQLite](https://img.shields.io/badge/SQLite-Offline--First-003B57?logo=sqlite)](https://sqlite.org/)
[![Firebase](https://img.shields.io/badge/Cloud%20Firestore-REST%20API-FFCA28?logo=firebase)](https://firebase.google.com/)

Aplicación multiplataforma (Android) desarrollada en **.NET MAUI** bajo arquitectura **Offline-First**, diseñada para la operativa técnica en campo sin dependencia de conectividad continua. Implementa un motor de persistencia local en SQLite sincronizado contra Google Cloud Firestore mediante escrituras atómicas, validación estricta de permisos y control de concurrencia optimista.

---

## 🏗️ Retos Técnicos y Arquitectura

### 1. Arquitectura Offline-First & Resiliencia de Red
* **Persistencia local transaccional:** Base de datos SQLite local configurada en modo `WAL` (Write-Ahead Logging) y nivel de sincronización `FULL` para garantizar la integridad de los datos ante cierres inesperados de la aplicación.
* **Cola de operaciones y resolución de conflictos:** Los registros de cosecha, tratamientos y fincas se consolidan localmente antes de iniciar el envío. Las escrituras hacia Firestore se procesan mediante precondiciones (`updateTime`) para evitar colisiones y sobreescrituras silenciosas entre dispositivos móviles concurrentes.
* **Consumo REST directo:** Comunicación directa con los endpoints oficiales de Cloud Firestore y Firebase Auth sin dependencias pesadas de servidor intermedio, implementando renovación automática de tokens JWT y control fino de timeouts.

### 2. Patrón MVVM & Desacoplamiento (Clean UI)
* Implementación del patrón **Model-View-ViewModel** mediante `CommunityToolkit.Mvvm`.
* Vistas desacopladas de la lógica de negocio; los ViewModels son agnósticos a controles de UI y al SDK de plataforma, facilitando la testabilidad unitaria.
* Inyección de dependencias (`Microsoft.Extensions.DependencyInjection`) configurada en el ciclo de arranque de la aplicación (`MauiProgram.cs`).

### 3. Seguridad y Control de Acceso (RBAC)
* Reglas de seguridad estrictas a nivel de base de datos (`firestore.rules`):
  * Aislamiento multi-tenant por explotación (`farms/{farmId}`).
  * Validación de esquemas y tipos directamente en servidor (reglas atómicas para creación y traspaso de membresías).
  * Control granular de roles: Propietario, Administrador, Operario y Lector.
* Las credenciales y tokens de sesión residen en el almacenamiento seguro nativo del sistema operativo (`SecureStorage`).

---

## 🛠️ Stack Tecnológico

* **Core:** .NET MAUI (.NET 10), C#
* **Arquitectura:** MVVM, Clean Architecture, Dependency Injection
* **Base de datos local:** SQLite con SQLite-net PCL
* **Cloud & Backend:** Firebase Authentication, Cloud Firestore (REST API)
* **Testing:** Pruebas unitarias de ViewModels, suite de comprobaciones transaccionales en C# y emulador oficial de reglas de Firebase

---

## 🧪 Pruebas Automatizadas y Calidad

El proyecto dispone de suites de validación independientes que pueden ejecutarse de manera local y en integración continua:

### Comprobaciones MVVM y Lógica Local:
```bash
dotnet run --project tests/Tunapp.MvvmChecks
dotnet run --project tests/Tunapp.Checks
Validación de Reglas de Seguridad en Emulador:
Bash
npm --prefix tests/firebase test
🚀 Compilación y Despliegue
Requisitos previos:
.NET SDK 10 o superior con carga de trabajo maui-android instalada.

Android SDK (API 30+).

Compilar APK en depuración:
Bash
dotnet build Tunapp.csproj -f net10.0-android -p:EmbedAssembliesIntoApk=true
👤 Autor
Javier Martínez

Full Stack Developer | .NET · React · PHP · Laravel

LinkedIn · Manfred
