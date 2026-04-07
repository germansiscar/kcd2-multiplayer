# validated_feature_list.md

## Estado de validación de features (Architect)

Fecha de actualización: 2026-04-07

## Fase A - Base persistente

### F01 / FT-001 - Contrato de persistencia JSON (IMPLEMENTADA)

- Estado: implementada en código + cubierta con tests.
- Reutiliza del repo actual:
  - `dotnet/KcdMp.Server/` como módulo destino de backend.
  - `dotnet/KcdMp.Tests/` para validación automática.
- Nueva implementación:
  - contrato `IJsonPersistenceStore`
  - implementación `JsonFilePersistenceStore`
  - opciones `JsonPersistenceOptions` y política de entorno (`Development`/`Production`)
  - excepciones detectables de carga/validación
- Refactor requerido: ninguno disruptivo (introducción aditiva en capa servidor).
- Riesgo técnico introducido:
  - acceso concurrente a un mismo documento aún no serializado por lock de dominio/id (pendiente en siguiente iteración si aumenta concurrencia).
- Evidencia:
  - tests en `dotnet/KcdMp.Tests/Persistence/JsonFilePersistenceStoreTests.cs`

### F02 / FT-002 - Estructura física de almacenamiento del servidor (IMPLEMENTADA)

- Estado: implementada en código + cubierta con tests.
- Reutiliza del repo actual:
  - `dotnet/KcdMp.Server/Persistence/JsonFilePersistenceStore.cs` (contrato FT-001 ya existente).
  - `dotnet/KcdMp.Tests/Persistence/JsonFilePersistenceStoreTests.cs` para validación automática.
- Nueva implementación:
  - catálogo de dominios canónicos en `JsonPersistenceDomains`
  - layout físico centralizado en `JsonServerStorageLayout`
  - bootstrap automático de carpeta raíz + dominios (`identity`, `characters`, `inventory`, `economy`, `audit`, `config`)
  - resolución de path por entidad con convención `/{domain}/{internalId}.json`
- Refactor requerido:
  - `JsonFilePersistenceStore` ahora delega totalmente la construcción de rutas al layout físico.
- Riesgo técnico introducido:
  - no hay lock por entidad/dominio para escrituras concurrentes (riesgo ya identificado en FT-001, aún vigente).
- Evidencia:
  - tests de persistencia actualizados en `dotnet/KcdMp.Tests/Persistence/JsonFilePersistenceStoreTests.cs`

### F03 / FT-003 - Backend de sesión base (IMPLEMENTADA)

- Estado: implementada en código + cubierta con tests.
- Reutiliza del repo actual:
  - `dotnet/KcdMp.Server/RelayServer.cs` y `dotnet/KcdMp.Server/ClientSession.cs` como base del relay y conexiones.
  - `dotnet/KcdMp.Tests/Integration/RelayServerTests.cs` para mantener cobertura de comportamiento de red existente.
- Nueva implementación:
  - capa `ServerSessionBackend` en `dotnet/KcdMp.Server/Sessions/`
  - modelo explícito de sesión (ID interno, estados, timestamps, auth/acceso, presencia, refs opcionales de identidad/personaje)
  - motivos de cierre explícitos (`NetworkDisconnect`, `Timeout`, `Shutdown`, `AuthenticationRejected`)
  - eventos identificables de lifecycle (`SessionCreated`, `AuthenticationAccepted`, `AuthenticationRejected`, `AssociationPending`, `SessionClosed`)
  - watchdog de timeout en servidor y cierre coordinado por shutdown
  - regla estructural de una identidad = una sesión activa mediante asociación controlada en backend
- Refactor requerido:
  - `RelayServer` ahora crea y coordina sesiones de backend por conexión TCP.
  - `ClientSession` ahora reporta actividad/auth al backend y expone cierre con motivo formal.
- Riesgo técnico introducido:
  - la política exacta de heartbeat/timeout es base y puede requerir ajuste en escenarios de red reales.
- Evidencia:
  - tests nuevos en `dotnet/KcdMp.Tests/Server/ServerSessionBackendTests.cs`

### F04 / FT-004 - Observabilidad base del servidor (IMPLEMENTADA)

- Estado: implementada en cÃ³digo + cubierta con tests.
- Reutiliza del repo actual:
  - `dotnet/KcdMp.Server/Sessions/ServerSessionBackend.cs` (hitos de lifecycle de FT-003).
  - Serilog ya existente en `dotnet/KcdMp.App/Program.cs`.
  - `dotnet/KcdMp.Server/Persistence/JsonFilePersistenceStore.cs` para eventos de carga/guardado.
- Nueva implementaciÃ³n:
  - modelo estructurado de eventos observables en `dotnet/KcdMp.Server/Observability/`.
  - sink desacoplado `IServerObservabilitySink` + implementaciÃ³n `SerilogServerObservabilitySink`.
  - emisiÃ³n de eventos de arranque/parada del relay.
  - emisiÃ³n de eventos de sesiÃ³n: creada, auth aceptada/rechazada, asociaciÃ³n pendiente, cierre y timeout.
  - emisiÃ³n de eventos de backend error y de persistencia (load/save success/fail).
  - control de nivel de detalle por severidad (`Debug`, `Information`, `Warning`, `Error`) configurable por `kcdmp.json`.
  - redacciÃ³n de claves sensibles en payload (`password`, `secret`, `token`, `credential`).
- Refactor requerido:
  - `RelayServer` pasa de relay puro a orquestador de observabilidad estructurada (sin acoplarse a destino concreto).
  - `ServerSessionBackend` expone notificaciÃ³n de lifecycle para observabilidad.
- Riesgo tÃ©cnico introducido:
  - si se baja demasiado el nivel de severidad podrÃ­a aumentar ruido operativo (mitigado por filtro de severidad y exclusiÃ³n de micro-sync por defecto).
- Evidencia:
  - tests nuevos en `dotnet/KcdMp.Tests/Server/ServerObservabilityTests.cs`.

### F05 / FT-005 - Identidad persistente (IMPLEMENTADA)

- Estado: implementada en código + cubierta con tests.
- Reutiliza del repo actual:
  - `dotnet/KcdMp.Server/Persistence/` (FT-001/FT-002) como capa JSON base.
  - `dotnet/KcdMp.Server/Sessions/ServerSessionBackend.cs` (FT-003) para regla de una identidad = una sesión activa.
  - `dotnet/KcdMp.Server/Observability/` (FT-004) para eventos estructurados de identidad.
- Nueva implementación:
  - módulo `dotnet/KcdMp.Server/Identity/` con:
    - modelo de identidad persistente (`PlayerIdentityRecord`)
    - estados (`Active`, `Blocked`, `Pending`)
    - claim de identificación híbrido (`SteamId`, `PersistentToken`, fallback de nombre)
    - servicio `PlayerIdentityService` con creación automática y resolución por índice persistente
  - persistencia JSON por entidad en dominio `identity` + índice técnico en `config/identity_lookup_v1.json`
  - integración en handshake del servidor (`ClientSession`) para:
    - resolver identidad
    - asociar sesión ↔ identidad
    - denegar bloqueados/pendientes (si whitelist requerida)
    - denegar segunda sesión concurrente de la misma identidad
  - extensión de observabilidad con eventos:
    - `IdentityResolved`
    - `IdentityCreated`
    - `IdentityAccessDenied`
    - `IdentityStatusChanged`
  - configuración base en `kcdmp.json`:
    - `identityRequireWhitelist`
- Refactor requerido:
  - `RelayServer` ahora compone persistencia + servicio de identidad como parte del backend.
  - `ClientSession` parsea claim de identidad desde handshake y valida acceso antes de entrar en estado ready.
- Riesgo técnico introducido:
  - fallback por nombre sigue siendo débil frente a suplantación (mitigado al priorizar SteamID/token cuando existan).
  - el índice externo→interno se protege con lock de proceso, pero no resuelve coordinación multi-proceso.
- Evidencia:
  - tests nuevos en:
    - `dotnet/KcdMp.Tests/Server/PlayerIdentityServiceTests.cs`
    - `dotnet/KcdMp.Tests/Integration/RelayServerTests.cs` (rechazo de sesión duplicada por identidad)

### F06 / FT-006 - Perfil persistente de personaje (IMPLEMENTADA)

- Estado: implementada en código + cubierta con tests.
- Reutiliza del repo actual:
  - `dotnet/KcdMp.Server/Persistence/` (FT-001/FT-002) para persistencia JSON por entidad.
  - `dotnet/KcdMp.Server/Identity/` (FT-005) para ownership identidad → personaje.
  - `dotnet/KcdMp.Server/Observability/` (FT-004) para eventos estructurados de personaje.
- Nueva implementación:
  - módulo `dotnet/KcdMp.Server/Characters/` con:
    - modelo persistente de personaje (`CharacterProfileRecord`)
    - estados (`Active`, `Inactive`, `Disabled`)
    - request/resultado de creación explícita (`CharacterCreateRequest`, `CharacterCreateResult`)
    - servicio `CharacterProfileService` + contrato `ICharacterProfileService`
  - unicidad global de nombre mediante índice persistente en `config/character_name_lookup_v1.json`
  - relación identidad → múltiples personajes mediante `identity.characterIds`
  - borrado lógico solo admin (`TryDeleteAsync(..., isAdminOperation: true)`) y deshabilitado normal (`TryDisableAsync`)
  - extensión de observabilidad con eventos:
    - `CharacterCreated`
    - `CharacterStatusChanged`
    - `CharacterDeleted`
    - `CharacterAccessDenied`
- Refactor requerido:
  - extensión no disruptiva del catálogo de eventos observables del servidor.
- Riesgo técnico introducido:
  - la unicidad global de nombre se protege por lock de proceso (sin coordinación multi-proceso todavía).
  - la aplicación de "un personaje activo por sesión" queda preparada en modelo/estados y se completa en FT-007.
- Evidencia:
  - tests nuevos en:
    - `dotnet/KcdMp.Tests/Server/CharacterProfileServiceTests.cs`

### F07 / FT-007 - VinculaciÃ³n sesiÃ³n â†” identidad â†” personaje (IMPLEMENTADA)

- Estado: implementada en cÃ³digo + cubierta con tests.
- Reutiliza del repo actual:
  - `dotnet/KcdMp.Server/Sessions/ServerSessionBackend.cs` (FT-003) como lifecycle/session context.
  - `dotnet/KcdMp.Server/Identity/` (FT-005) para resoluciÃ³n de identidad.
  - `dotnet/KcdMp.Server/Characters/CharacterProfileService.cs` (FT-006) para validaciÃ³n de personaje y estados.
- Nueva implementaciÃ³n:
  - servicio `CharacterSessionBindingService` + contrato `ICharacterSessionBindingService`.
  - reglas de binding:
    - validaciÃ³n de ownership `identity_id -> character_id`
    - rechazo de personaje eliminado/deshabilitado
    - auto-selecciÃ³n opcional de personaje cuando no llega `characterId`
    - activaciÃ³n del personaje seleccionado e inactivaciÃ³n del resto de la identidad
  - integraciÃ³n en handshake del servidor:
    - parseo de `characterId` (y `activeCharacterId`) en `ClientSession`
    - orquestaciÃ³n de identidad + binding en `RelayServer` antes de emitir `Ack`
    - persistencia de referencia `session.characterId`
  - rollback explÃ­cito de asociaciÃ³n de identidad si falla el binding de personaje (`ClearIdentityAssociation`).
  - extensiÃ³n de cliente/config para enviar claim enriquecido en handshake:
    - `persistentToken`
    - `steamId`
    - `characterId`
    - `characterRequireOnConnect`
- Refactor requerido:
  - `RelayServer` ahora compone tambiÃ©n el servicio de binding de personaje.
  - `ClientLauncher`/`GameBridge` envÃ­an handshake JSON cuando hay datos de identidad/personaje.
- Riesgo tÃ©cnico introducido:
  - sin UI de selecciÃ³n, la auto-selecciÃ³n puede no coincidir con la intenciÃ³n del jugador (mitigable con `characterId` explÃ­cito).
  - locks de proceso (no distribuidos) se mantienen como limitaciÃ³n de esta etapa.
- Evidencia:
  - tests nuevos en:
    - `dotnet/KcdMp.Tests/Server/CharacterSessionBindingServiceTests.cs`
    - `dotnet/KcdMp.Tests/Integration/RelayServerTests.cs` (binding por handshake JSON y rechazo cuando personaje es obligatorio)
    - `dotnet/KcdMp.Tests/Server/ServerSessionBackendTests.cs` (`ClearIdentityAssociation`)

### F08 / FT-008 - Ciclo de carga/guardado de personaje (IMPLEMENTADA)

- Estado: implementada en codigo + cubierta con tests.
- Reutiliza del repo actual:
  - `dotnet/KcdMp.Server/Characters/CharacterProfileService.cs` (FT-006) para validacion de ownership/estado.
  - `dotnet/KcdMp.Server/Sessions/ServerSessionBackend.cs` (FT-003) para asociacion de `session_id`.
  - `dotnet/KcdMp.Server/Observability/` (FT-004) para trazabilidad de lifecycle.
- Nueva implementacion:
  - servicio `CharacterLifecycleService` + contrato `ICharacterLifecycleService`.
  - validacion ligera previa de personaje (existencia, ownership, estado usable).
  - carga completa en memoria por sesion al finalizar binding.
  - bloqueo de doble carga del mismo personaje en sesiones simultaneas.
  - guardado explicito con reintento controlado y causas (`Activation`, `SessionClosed`, `NetworkDisconnect`, `SessionTimeout`, `Shutdown`).
  - guardado en cierre de sesion y flush de personajes activos en shutdown ordenado.
  - extension de observabilidad con eventos:
    - `CharacterLoadStarted`
    - `CharacterLoadCompleted`
    - `CharacterLoadFailed`
    - `CharacterSaveStarted`
    - `CharacterSaveCompleted`
    - `CharacterSaveFailed`
- Refactor requerido:
  - `RelayServer` orquesta validacion/carga de personaje antes de marcar la sesion como preparada.
  - `RelayServer` ejecuta guardado/unload al cierre de sesion y en parada del servidor.
- Riesgo tecnico introducido:
  - el lock de personaje activo es in-process (no distribuido multi-nodo).
  - no existe aun cola avanzada de recuperacion tras fallo persistente repetido de guardado.
- Evidencia:
  - tests nuevos en:
    - `dotnet/KcdMp.Tests/Server/CharacterLifecycleServiceTests.cs`

### F09 / FT-009 - Inventario persistente base (IMPLEMENTADA)

- Estado: implementada en codigo + cubierta con tests.
- Reutiliza del repo actual:
  - `dotnet/KcdMp.Server/Persistence/` (FT-001/FT-002) para persistencia JSON por dominio.
  - `dotnet/KcdMp.Server/RelayServer.cs` para integracion de lifecycle en conexion/cierre/shutdown.
  - `dotnet/KcdMp.Server/Observability/` (FT-004) para eventos estructurados de inventario.
- Nueva implementacion:
  - modulo `dotnet/KcdMp.Server/Inventory/` con:
    - modelo persistente de inventario por `character_id` (`CharacterInventoryRecord`)
    - contenedores independientes por personaje (`InventoryContainerRecord`)
    - items apilables/no apilables con metadata y flags (`InventoryItemRecord`)
    - servicio `CharacterInventoryService` + contrato `ICharacterInventoryService`
  - carga en memoria por sesion con fuente operativa en servidor.
  - contenedor base obligatorio (`main`) incluso para inventario vacio.
  - validacion minima y tolerancia a corrupcion parcial (se excluyen items/contenedores invalidos sin bloquear la carga).
  - guardado por eventos y en cierre/desconexion/shutdown, con retry controlado.
  - observabilidad extendida con eventos:
    - `InventoryLoadStarted` / `InventoryLoadCompleted` / `InventoryLoadFailed`
    - `InventorySaveStarted` / `InventorySaveCompleted` / `InventorySaveFailed`
    - `InventoryChanged`
    - `InventoryValidationFailed`
- Refactor requerido:
  - `RelayServer` ahora orquesta tambien load/save/unload de inventario junto al lifecycle de personaje.
- Riesgo tecnico introducido:
  - coherencia de inventario en memoria sigue siendo in-process (sin coordinacion multi-nodo en esta etapa).
  - no existe aun logica de transferencia/loot/equipamiento (queda para features siguientes por separacion de responsabilidad).
- Evidencia:
  - tests nuevos en:
    - `dotnet/KcdMp.Tests/Server/CharacterInventoryServiceTests.cs`

### F12 / FT-012 - Configuracion persistente de reglas de inventario (IMPLEMENTADA)

- Estado: implementada en codigo + cubierta con tests.
- Reutiliza del repo actual:
  - `dotnet/KcdMp.Server/Persistence/` (FT-001/FT-002) para persistencia de policy y estado aplicado.
  - `dotnet/KcdMp.Server/Respawn/CharacterRespawnService.cs` (FT-011) para activar reglas por estados post-derrota.
  - `dotnet/KcdMp.Server/Observability/` (FT-004) para trazabilidad estructurada de config/aplicacion.
- Nueva implementacion:
  - modulo `dotnet/KcdMp.Server/InventoryRules/` con:
    - configuracion persistente del servidor (`InventoryRulesConfigurationRecord`)
    - estado aplicado por personaje (`CharacterInventoryRuleStateRecord`)
    - reglas de acceso por contenedor (`InventoryContainerRuleRecord`)
    - servicio `InventoryRulesConfigurationService` + contrato `IInventoryRulesConfigurationService`
  - default canonico al arrancar:
    - inventario persistente
    - no lootable por defecto
  - compatibilidad con extension por tipo de contenedor y modelo de acceso por:
    - llave valida
    - contenedor completamente abierto
    - forzado de cerradura (resultado externo)
  - regla explicita de baules no afectados por muerte (`AffectedByDefeat = false` en regla base `chest`).
  - aplicacion de reglas integrada en lifecycle de respawn:
    - entrada en inconsciencia
    - recuperacion por sanador
    - respawn aplicado / pendiente de respawn
  - persistencia inmediata del resultado aplicado por personaje en dominio `inventory_rules`.
  - bootstrap en `RelayServer` con carga de configuracion al arranque (sin cambios en caliente).
  - observabilidad extendida con eventos:
    - `InventoryRulesConfigLoaded`
    - `InventoryRulesConfigValidationFailed`
    - `InventoryRuleApplied`
    - `InventoryLootabilityChanged`
    - `InventoryRuleApplyFailed`
- Refactor requerido:
  - `RelayServer` ahora inicializa y coordina el servicio de reglas de inventario en preparacion de sesion.
  - `CharacterRespawnService` aplica policy de inventario al transicionar estados de derrota/respawn.
- Riesgo tecnico introducido:
  - FT-012 no implementa interaccion real de loot entre jugadores (se mantiene para FT-018 por separacion de responsabilidad).
  - la evaluacion de acceso por forzado depende de un resultado externo (minijuego adaptado) aun no integrado en backend.
- Evidencia:
  - tests nuevos en:
    - `dotnet/KcdMp.Tests/Server/InventoryRulesConfigurationServiceTests.cs`

### F13 / FT-013 - Control de acceso persistente (IMPLEMENTADA)

- Estado: implementada en codigo + cubierta con tests.
- Reutiliza del repo actual:
  - `dotnet/KcdMp.Server/Identity/` (FT-005) para resolucion y persistencia de identidad.
  - `dotnet/KcdMp.Server/Sessions/ServerSessionBackend.cs` (FT-003) para regla de una identidad = una sesion activa.
  - `dotnet/KcdMp.Server/Observability/` (FT-004) para eventos estructurados.
- Nueva implementacion:
  - modulo `dotnet/KcdMp.Server/AccessControl/` con:
    - configuracion persistente de acceso (`ServerAccessControlConfigurationRecord`) en `config/access_control_v1.json`
    - modos `Open` y `Whitelist`
    - evaluacion canonica servidor-side por estado de identidad (`Active`, `Blocked`, `Pending`)
    - decision de acceso con motivos explicitos y observabilidad de permitido/denegado
  - integracion en handshake del servidor (`RelayServer`):
    - carga de policy al arranque
    - resolucion/creacion de identidad segun modo de acceso
    - denegacion de bloqueados
    - denegacion de pendientes en whitelist
    - alta de identidad nueva como `Pending` en whitelist y rechazo inmediato
    - alta de identidad nueva en abierto y acceso permitido
    - rechazo de sesion duplicada por identidad activa
  - extension de configuracion de app:
    - `serverAccessMode` (con fallback legacy de `identityRequireWhitelist`)
  - extension de observabilidad con eventos:
    - `AccessControlConfigLoaded`
    - `AccessControlConfigValidationFailed`
    - `AccessDecisionAllowed`
    - `AccessDecisionDenied`
- Refactor requerido:
  - `PlayerIdentityService` deja de aplicar denegacion por whitelist; ahora resuelve/crea identidad y el acceso final se decide en `AccessControl`.
- Riesgo tecnico introducido:
  - el cambio en modo de acceso sigue siendo por reinicio (sin hot-reload), intencional en esta fase.
  - la configuracion persistente es in-process y no contempla coordinacion multi-nodo.
- Evidencia:
  - tests nuevos en:
    - `dotnet/KcdMp.Tests/Server/ServerAccessControlServiceTests.cs`
    - `dotnet/KcdMp.Tests/Integration/RelayServerTests.cs` (casos whitelist/open para pendientes/nuevos)

### F14 / FT-014 - Bans persistentes por identidad (IMPLEMENTADA)

- Estado: implementada en codigo + cubierta con tests.
- Reutiliza del repo actual:
  - `dotnet/KcdMp.Server/Persistence/` (FT-001/FT-002) para persistencia JSON por dominio.
  - `dotnet/KcdMp.Server/AccessControl/` (FT-013) para evaluacion de acceso durante handshake.
  - `dotnet/KcdMp.Server/Observability/` + `dotnet/KcdMp.Server/Audit/` (FT-004/FT-015) para trazabilidad estructurada.
- Nueva implementacion:
  - modulo `dotnet/KcdMp.Server/Bans/` con:
    - modelo persistente de historial de bans por `identity_id` (`IdentityBanHistoryRecord`)
    - entradas de sancion (`IdentityBanEntry`) con tipo (`Temporary`/`Permanent`) y estado (`Active`/`Expired`/`Revoked`)
    - servicio `IdentityBanService` + contrato `IIdentityBanService`
  - dominio de persistencia dedicado `bans` en layout JSON del servidor.
  - aplicacion de sancion con motivo+actor obligatorios y soporte de notas/referencias auditables.
  - regla de unicidad: una sola sancion activa por identidad.
  - expiracion automatica de bans temporales al evaluar acceso o consultar estado.
  - revocacion manual trazable con actor y motivo de revocacion.
  - denegacion de acceso por ban activo con mensaje resumido para cliente.
  - expulsion inmediata de identidad conectada al aplicar ban activo (kick de sesion en caliente).
  - observabilidad extendida con eventos:
    - `BanApplied`
    - `BanRevoked`
    - `BanExpired`
    - `BanAccessDenied`
    - `BanSessionKicked`
- Refactor requerido:
  - `RelayServer` ahora compone `IdentityBanService` y evalua bans antes de completar asociacion de identidad en handshake.
  - `ServerSessionBackend` expone busqueda de sesion activa por identidad para permitir expulsiones inmediatas.
  - `PersistentAuditObservabilitySink` incorpora el lifecycle de bans en categoria de acceso.
- Riesgo tecnico introducido:
  - la coordinacion de bans/sesiones es in-process (sin coordinacion multi-nodo en esta fase).
  - la aplicacion de bans en caliente depende del canal administrativo que invoque `RelayServer.ApplyIdentityBanAsync` (UI admin queda para FT-016).
- Evidencia:
  - tests nuevos en:
    - `dotnet/KcdMp.Tests/Server/IdentityBanServiceTests.cs`
    - `dotnet/KcdMp.Tests/Integration/RelayServerTests.cs` (rechazo por ban y expulsion inmediata en sesion activa)
    - `dotnet/KcdMp.Tests/Server/PersistentAuditObservabilitySinkTests.cs` (ban auditado en categoria Access)

### F16 / FT-016 - Administracion base del servidor RP (IMPLEMENTADA)

- Estado: implementada en codigo + cubierta con tests.
- Reutiliza del repo actual:
  - `dotnet/KcdMp.Server/Identity/` (FT-005) para identidad persistente y control de estado.
  - `dotnet/KcdMp.Server/Characters/` (FT-006/FT-007/FT-008) para operaciones administrativas sobre personajes.
  - `dotnet/KcdMp.Server/Bans/` (FT-014) para aplicar/revocar sanciones persistentes.
  - `dotnet/KcdMp.Server/Audit/` (FT-015) para trazabilidad persistente de acciones admin.
  - `dotnet/KcdMp.Server/Sessions/` (FT-003) para inspeccion y expulsion de sesiones activas.
- Nueva implementacion:
  - modulo `dotnet/KcdMp.Server/Admin/` con:
    - contrato `IServerAdminService`
    - modelos de comandos y resultados admin
    - servicio `ServerAdminService` con operaciones de:
      - whitelist (listar pendientes, aprobar, rechazar)
      - identidad (consulta, cambio de estado, desactivacion)
      - personajes (listar, deshabilitar, archivar, borrar admin)
      - sesiones (listar, inspeccionar, expulsar)
      - bans (listar, detalle, aplicar, revocar)
      - auditoria (consulta por identidad, personaje, rango temporal y tipo de evento)
  - control de permisos por rol admin mediante `PlayerIdentityRole` (`Player`/`Admin`).
  - bootstrap operativo de admins en `RelayServer` via `bootstrapAdminIdentityIds` y config `AdminIdentityIds`.
  - observabilidad extendida con eventos:
    - `WhitelistApproved`
    - `WhitelistRejected`
    - `IdentityRoleChanged`
    - `AdminSessionKicked`
    - `AdminAuditQueried`
  - integracion de eventos FT-016 en `PersistentAuditObservabilitySink`.
- Refactor requerido:
  - `RelayServer` ahora compone y expone `IServerAdminService` y encapsula kick por `session_id`.
  - `IPlayerIdentityService` se amplia con `TrySetRoleAsync`.
- Riesgo tecnico introducido:
  - consulta de bans/auditoria por escaneo de archivos JSON (in-process); puede requerir indexado futuro para volumen alto.
  - la notificacion resumida al jugador expulsado se limita al cierre de sesion en esta fase (sin canal dedicado de mensajeria admin).
- Evidencia:
  - tests nuevos en:
    - `dotnet/KcdMp.Tests/Server/ServerAdminServiceTests.cs`

### F20 / FT-020 - Aplicacion de estado dirigida por servidor (IMPLEMENTADA)

- Estado: implementada en codigo + cubierta con tests.
- Reutiliza del repo actual:
  - `dotnet/KcdMp.Server/RelayServer.cs` como orquestador de sesion y canon persistente.
  - `dotnet/KcdMp.Client/GameBridge.cs` como adaptador local hacia runtime del juego.
  - `kdcmp/Data/Scripts/Startup/kdcmp.lua` como ejecutor local dentro de capacidades verificadas.
  - `dotnet/KcdMp.Server/Observability/` (FT-004) para trazabilidad del lifecycle de proyeccion.
- Nueva implementacion:
  - extension de protocolo compartido en `dotnet/KcdMp.Shared/Protocol/`:
    - `PacketType.StateProjection` (0x0D)
    - `PacketType.StateProjectionResult` (0x0E)
    - enums `ProjectionDomain`, `ProjectionApplicability`, `ProjectionApplyStatus`
    - serializacion/parsing dedicado en `PacketWriter` y `PacketReader`
  - proyeccion canonica inicial servidor -> cliente al finalizar handshake:
    - dominios: `SessionCharacter`, `Presence`, `LifeCycle`, `Inventory`, `Currency`, `Administrative`
    - el servidor mantiene canon persistente y emite aplicabilidad (`Direct`, `Partial`, `NotApplicableYet`)
  - reporte cliente -> servidor del ciclo de aplicacion:
    - `Started`, `Applied`, `PartiallyApplied`, `NotApplied`, `Failed`
    - adaptacion local por dominio en `GameBridge` + funciones Lua de proyeccion
  - gestion de fallo con reintento controlado:
    - incidente observable
    - reenvio controlado de proyeccion fallida/no aplicada (`maxRetries` base)
  - deteccion de desalineacion:
    - eventos de observabilidad cuando la aplicacion es parcial/no aplicada/fallida
- Refactor requerido:
  - `ClientSession` ahora inicia proyeccion de estado canonico tras `Ack` y procesa `StateProjectionResult`.
  - `RelayServer` incorpora tracking de proyecciones pendientes por sesion para retry y limpieza en cierre.
- Riesgo tecnico introducido:
  - la aplicacion local sigue limitada por debug REST API + capacidades del runtime (hibrido intencional).
  - la logica de retry y tracking de proyeccion es in-process (sin coordinacion multi-nodo en esta etapa).
- Evidencia:
  - tests actualizados/nuevos en:
    - `dotnet/KcdMp.Tests/Protocol/StateEventPacketTests.cs`
    - `dotnet/KcdMp.Tests/Integration/RelayServerTests.cs`

### F21 / FT-021 - Adaptacion de cliente a identidad/personaje persistentes (IMPLEMENTADA)

- Estado: implementada en codigo + cubierta con tests.
- Reutiliza del repo actual:
  - `dotnet/KcdMp.Client/GameBridge.cs` (FT-020) para consumo de `StateProjection`.
  - `dotnet/KcdMp.Server/RelayServer.cs` para proyecciones administrativas y contexto inicial.
  - `dotnet/KcdMp.Shared/Protocol/` para dominios/status de proyeccion.
- Nueva implementacion:
  - estado derivado explicito del cliente en `dotnet/KcdMp.Client/ClientDerivedState.cs`:
    - `session_id`, `identity_id`, `character_id`, `readiness`
    - estado administrativo (`accessMode`, `identityStatus`, `characterStatus`, ban/kick/accessDenied)
    - fase de adaptacion (`Pending`, `Partial`, `Complete`, `Error`)
  - `GameBridge` ahora:
    - valida respuestas de auth/handshake (incluyendo rechazo por `AuthResult`)
    - inicia cada reconexion como ciclo nuevo (`BeginNewCycle`)
    - marca y reporta estado de aplicacion por dominio
    - limpia flujo local por invalidacion y muestra mensajeria resumida
    - tolera aplicacion parcial/fallida sin promover canon local
  - `RelayServer` ahora proyecta:
    - `readiness` y `characterStatus` en contexto inicial
    - proyeccion administrativa de invalidacion en kick/ban antes de cierre de sesion
- Refactor requerido:
  - `KcdMp.Tests` referencia tambien `KcdMp.Client` para validar estado derivado.
- Riesgo tecnico introducido:
  - la notificacion de invalidacion previa al cierre depende del flush de red del canal activo (best-effort).
  - la adaptacion del runtime Lua profundo permanece fuera de FT-021 (queda en FT-022).
- Evidencia:
  - tests nuevos en:
    - `dotnet/KcdMp.Tests/Client/ClientDerivedStateTests.cs`
  - tests de integracion actualizados:
    - `dotnet/KcdMp.Tests/Integration/RelayServerTests.cs` (ban en caliente proyecta estado administrativo antes del cierre)
