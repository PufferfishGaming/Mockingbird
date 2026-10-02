# Política de privacidad de Mockingbird Studio

Fecha de entrada en vigor: 1 de octubre de 2026

## Procesamiento local
Mockingbird Studio transcribe las grabaciones y corrige las transcripciones en su equipo. La aplicación no sube archivos multimedia ni transcripciones a ningún servicio de transcripción. No tiene analíticas integradas, sistema de cuentas, publicidad ni envío automático de informes de fallos.

## Archivos almacenados en su equipo
Los proyectos pueden contener la ruta y el hash del archivo multimedia de origen, audio normalizado, la salida sin procesar de los motores, transcripciones, los cambios que usted hace durante la revisión, puntos de control, mediciones de hardware y registros de la aplicación. Los modelos, las descargas parciales, los registros de las comprobaciones de suma de verificación y los ajustes se guardan en local. Estos archivos pueden incluir información personal procedente de sus grabaciones o de las rutas de archivo. La aplicación no los cifra.

Las versiones instaladas de la aplicación usan el directorio heredado LocalAppData/TriASR, a menos que elija otra carpeta de proyectos o de modelos. Así se conservan las instalaciones existentes. Desinstalar la aplicación deja en su sitio los proyectos, los ajustes y los modelos descargados. Para eliminar sus datos, cierre la aplicación y borre usted mismo las carpetas elegidas. Conserve las grabaciones o exportaciones que desee mantener.

## Conexiones de red
Cuando solicita descargas de modelos o de entornos de ejecución, la aplicación se conecta a Hugging Face o a GitHub y a su infraestructura de descarga. Esos proveedores reciben la información de conexión habitual, como su dirección IP y el recurso solicitado. El contenido de los archivos multimedia y de las transcripciones no se incluye en estas solicitudes de descarga. A sus servicios se aplican las políticas de privacidad de los proveedores:
- https://huggingface.co/privacy
- https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement

### Comprobaciones de actualizaciones
A menos que lo desactive en Ajustes, la aplicación pide a GitHub un pequeño archivo de versión (latest.json) al iniciarse, como máximo cada 12 horas, y siempre que elija Buscar actualizaciones ahora. GitHub recibe la información de conexión habitual, como su dirección IP, y la solicitud incluye el nombre y la versión de la aplicación. No se envían grabaciones, transcripciones, datos de proyectos, detalles del hardware ni identificadores. La aplicación nunca instala una actualización por sí sola: usted elige Actualizar ahora, y el instalador descargado se comprueba con su suma de comprobación SHA256 publicada antes de ejecutarse.

El servidor de corrección local se comunica a través de la interfaz de bucle local (127.0.0.1); no es inferencia en la nube. La descarga del instalador de la aplicación también se conecta a GitHub. Una vez instalados los modelos y entornos de ejecución necesarios, la transcripción puede funcionar sin conexión.

## Terminal, exportaciones y soporte
Los comandos que introduce en el panel de PowerShell tienen los permisos de su usuario de Windows. Pueden acceder a archivos, contactar con servicios de red o transmitir datos según los comandos que ejecute. La declaración de procesamiento local de esta política describe las funciones de transcripción de la aplicación, no comandos arbitrarios del terminal.

La aplicación no envía registros automáticamente. Revise el diagnóstico, la salida de los motores y las transcripciones antes de compartirlos en las incidencias de GitHub. Copiar o exportar datos es algo que inicia usted. Su sistema operativo, las copias de seguridad, las carpetas sincronizadas, el software de seguridad y los entornos de ejecución de terceros pueden tratar los archivos según sus propias políticas.

## Cambios y preguntas
Las versiones futuras pueden revisar esta política. Consulte la política incluida con la versión que instale. Las preguntas sobre privacidad pueden plantearse a través de las incidencias de GitHub del proyecto sin adjuntar grabaciones, transcripciones ni registros privados.
