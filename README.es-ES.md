<div align="right">

[English](README.md) · [Українська](README.uk-UA.md) · **Español** · [Русский](README.ru-RU.md)

</div>

# Cities: Skylines 2 — Patcher para macOS / Wine

Corrige fallos y habilita Paradox Mods para **Cities: Skylines 2** ejecutándose bajo CrossOver en macOS.

Probado: **CrossOver 26 · Juego v1.5.8f1–v1.6.0f1 · Apple Silicon (M3 Pro → M5 Max)**

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
cp Backtrace.Unity.dll.bak Backtrace.Unity.dll
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

## Créditos

Este parche se apoya en el trabajo de:

- **[alexqzd/cs2-crossover-patcher](https://github.com/alexqzd/cs2-crossover-patcher)** — el
  parche original para CrossOver y las correcciones base de `Colossal.IO.dll`,
  `Colossal.IO.AssetDatabase.dll` y Paradox Mods.
- **[icetear/cs2-net-snap-fix](https://github.com/icetear/cs2-net-snap-fix)** — el
  descubrimiento de la causa raíz del error de fijación de redes elevadas (Rosetta compilando
  incorrectamente el código SIMD Burst de Unity), sobre el que se construye la corrección de
  este parche.

Gracias a ambos por haberlo resuelto primero.
