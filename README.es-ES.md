

# Cities: Skylines 2 — Patcher para macOS / Wine

Corrige fallos y habilita Paradox Mods para **Cities: Skylines 2** ejecutándose bajo CrossOver en macOS.

Probado: **CrossOver 26 · Juego v1.5.8f1–v1.6.0f1 · Apple Silicon (M3 Pro → M5 Max)**

> **¿Las redes elevadas quedan pegadas al suelo?** Un error de Apple Silicon (Rosetta descompila incorrectamente el código SIMD Burst de Unity y omite el valor de altura) hace que las carreteras elevadas, puentes y tuberías se fijen incorrectamente en lo que haya debajo. **Este parche lo corrige** — sin la regresión de FPS del parche independiente [icetear/cs2-net-snap-fix](https://github.com/icetear/cs2-net-snap-fix) (ver [docs/technical.md](docs/technical.md)).

---

## Cómo usarlo

Abre Terminal, pega esto y presiona Enter:

```bash
git clone https://github.com/alien-agent/cs2-macos-patcher && cd cs2-macos-patcher && ./patch.py
```

`patch.py` es una herramienta **guiada e interactiva**. Te guía a través de:

1. **Encuentra tu juego** automáticamente en todas las botellas de CrossOver (incluidas las carpetas de botellas personalizadas configuradas en los ajustes de CrossOver) e indica si ya está parcheado
2. **Muestra una vista previa del cambio** (una ejecución de prueba que no escribe nada) y luego te pide confirmar
3. **Aplica todas las correcciones** — inicio, activos, menú de pausa, fijación de redes, Paradox Mods — y hace una copia de seguridad de los originales en `*.bak`
4. Instala dotnet automáticamente a través de Homebrew si es necesario

> **¿Sin dotnet?** No hay problema, el parche lo instala por ti. Solo necesitas [Homebrew](https://brew.sh).
>
> Si dotnet ya está instalado, el parche verifica que pueda compilar las correcciones (SDK 9 o más reciente) e instala un SDK actual a través de Homebrew si el tuyo es demasiado antiguo (por ejemplo, un .NET 6 residual).

### Tras una actualización del juego

Vuelve a ejecutar `./patch.py` y aplica el parche de nuevo. Tanto la vista previa como el parche detectan los archivos ya parcheados y los omiten, luego aplican cualquier corrección nueva a los DLL actualizados: es siempre seguro volver a ejecutarlo.

### ¿No se encuentra el juego automáticamente?

Pasa la carpeta Managed directamente:

```bash
./patch.py "/path/to/Cities2_Data/Managed"
```

La carpeta Managed suele estar dentro de tu botella de CrossOver:

```
~/Library/Application Support/CrossOver/Bottles/<bottle-name>/drive_c/
  Program Files (x86)/.../Cities2_Data/Managed
```

### Restaurar los DLL originales

Ejecuta `./patch.py` y selecciona **Restaurar archivos originales**: copiará cada `*.bak` de nuevo sobre su DLL.

¿Prefieres hacerlo a mano? Las copias de seguridad son archivos planos:

```bash
cd "<path-to>/Cities2_Data/Managed"
cp Colossal.IO.dll.bak Colossal.IO.dll
cp Colossal.IO.AssetDatabase.dll.bak Colossal.IO.AssetDatabase.dll
cp Game.dll.bak Game.dll
cp PDX.SDK.dll.bak PDX.SDK.dll
```

---

## Configuración de CrossOver para el mejor rendimiento

Mi recomendación personal para los mejores gráficos/rendimiento en CrossOver 26:

| Setting                       | Value                | Notes                                                                                                                                                                                               |
|-------------------------------|----------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Gráficos**                  | **D3DMetal**         | CS2 usa DirectX 12. D3DMetal (del Apple Game Porting Toolkit) es el único traductor que soporta DX12 correctamente. DXVK y wined3d son más lentos o están rotos para DX12. DXMT es solo para DX11: no lo uses. |
| **Sincronización**           | **MSync**            | Sincronización basada en semáforos Mach. Confirmado como mejor que ESync para CS2.                                                                                                                                     |
| **DLSS (impulsado por MetalFX)** | **Habilitado**          | Nuevo en CrossOver 26. Requiere que DLSS también esté habilitado dentro del juego. Ganancia significativa de FPS en Apple Silicon.                                                                                       |
| **Modo de Alta Resolución**      | **Activado**               | Desactiva el duplicado de píxeles: comportamiento correcto en pantallas Retina.                                                                                                                                     |
| **Versión de Windows**           | **Windows 10 o 11** | No uses XP o 7: rompen las funciones del runtime .NET de las que depende el juego.                                                                                                                           |
| **AVX**                       | **Habilitado**          | CrossOver 25+ expone AVX al juego mediante `ROSETTA_ADVERTISE_AVX=1`. Mejora el rendimiento en Apple Silicon bajo Rosetta.                                                                           |

> **macOS Tahoe (26)** ofrece el mejor soporte para Metal 4 y todos los beneficios de DLSS/MetalFX. Bajo macOS
> Sequoia (15.x) algunas características de Metal 4 no están disponibles.

---

## Configuración gráfica dentro del juego

Estos ajustes marcan la mayor diferencia para el rendimiento dentro del propio CS2.

**Ajustes básicos:**

| Setting                    | Value                                                                   | Notes                                                      |
|----------------------------|-------------------------------------------------------------------------|------------------------------------------------------------|
| **Modo de visualización**           | **Pantalla completa en ventana**                                                 | Más rápido que Pantalla completa exclusiva                           |
| **Resolución**             | **1080p o 1440p**                                                      | No uses la resolución nativa de Retina: arruina el rendimiento |
| **VSync**                  | **Desactivado**                                                            |                                                            |
| **Preferencia de rendimiento** | **Tasa de fotogramas**                                                          |                                                            |
| **Resolución dinámica**     | **DLSS Equilibrado** (si MetalFX está activado arriba), de lo contrario **Calidad FSR** |                                                            |
| **Profundidad de campo**         | **Desactivado**                                                            | Uno de los efectos más pesados en CS2                         |
| **Desenfoque de movimiento**            | **Desactivado**                                                            | Un buen impulso de rendimiento sin coste                             |

---

## Detalles técnicos

Para una explicación completa de cada error de Wine que este parche evita y cómo funciona cada corrección a nivel de IL, consulta [docs/technical.md](docs/technical.md).

---

## Créditos y trabajos previos

Este parche se basa
en [alexqzd/cs2-crossover-patcher](https://github.com/alexqzd/cs2-crossover-patcher), que proporcionó
las correcciones base para `Colossal.IO.dll`, `Colossal.IO.AssetDatabase.dll` y los parches iniciales de Paradox
Mods.

**Lo que añade este parche en comparación con alexqzd:**

- **Soporte de Paradox Mods para v1.5.8f1+.** El parche de alexqzd dejó de funcionar tras las actualizaciones v1.5.6+.
  Se identificaron y corrigieron adecuadamente dos errores raíz:
    1. `FileIO.GetLockToken`: un temporizador waitable de Win32 para un tiempo de espera de bloqueo de 10 segundos se activa en
       milisegundos bajo Wine, cancelando cada descarga antes de que comience.
    2. `FileIO.<CreateFileStream>.MoveNext`: `File.Exists` de Wine devuelve `true` para archivos inexistentes, lo que hace que el código adquiera un bloqueo de lectura, falle al abrir el archivo y salga del
       controlador de excepciones sin liberar el bloqueo. Todos los intentos de escritura posteriores para la misma ruta
       se quedan colgados indefinidamente.
- **Corrección del menú de pausa en el juego (`Game.dll`).** La sonda de Rider-IDE de la cadena de herramientas de modificación lanza una excepción debido a los `File.Exists`/`Directory.Exists`
  falsos de Wine durante la carga; esa excepción lanzada impide que se abra el
  **menú de pausa de Esc / engranaje**. Forzar las dos comprobaciones de existencia a `false` (la
  realidad bajo Wine) detiene el lanzamiento y el menú funciona.
- **Corrección de fijación de redes elevadas (`Game.dll`).** En Apple Silicon, Rosetta rompe la comprobación de altura SIMD Burst,
  por lo que los puentes/líneas eléctricas/tuberías se fijan en las estructuras de abajo. La corrección ejecuta
  las tareas de fijación de la herramienta de redes en la ruta administrada (correcta) **solo mientras la herramienta está activa** — sin
  conmutación global de Burst, coste cero cuando la herramienta está cerrada. Mismo insight de causa raíz que
  [icetear/cs2-net-snap-fix](https://github.com/icetear/cs2-net-snap-fix), pero sin la
  regresión de rendimiento reportada.
- **Corrección del diálogo falso `IOException: …Success` (`Colossal.IO.dll`).** Wine informa una apertura de archivo fallida con código de error `0` ("Success") en lugar de "archivo no encontrado", por lo que leer un archivo de configuración ausente (p. ej. `Benchmark.coc`) muestra una superposición de error en el juego en lugar de manejarse
  en silencio. La corrección remapea el código de error 0 de Wine a "archivo no encontrado" para que el controlador existente del juego
  lo ignore.
- **Corrección de Paradox Launcher 2026.8+.** El lanzador se actualiza silenciosamente y el Chromium de la nueva versión
  no puede crear ningún contexto de GPU bajo Wine: la ventana del lanzador nunca se abre y el
  juego "no arranca". `patch.py` añade automáticamente banderas de SwiftShader (renderizado por software) a
  las opciones de inicio de Steam de CS2, corrigiendo el lanzador sin modificar archivos de Paradox. (Ejecuta
  `./patch.py` con Steam cerrado para que este paso se aplique.)
- **Comando único guiado** — `./patch.py` maneja todo: una **vista previa de ejecución de prueba antes
  de aplicar**, **restauración** desde el menú y instalación automática de dotnet.
- **Detección automática** del juego en todas las botellas de CrossOver.
- **Cada corrección documentada en su archivo fuente** — cada parche reside en
  [`cs2patcher/Fixes/`](cs2patcher/Fixes/) en un archivo con el nombre del problema que corrige, con la
  descripción completa de la causa raíz en su encabezado; [docs/technical.md](docs/technical.md) es el índice.
