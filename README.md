# apiAKS_NetCore

Proyecto minimal en .NET 8 que expone un endpoint POST `/echo`.

Instrucciones rápidas:

- **Restaurar y compilar**:

```powershell
dotnet build
```

- **Ejecutar** (por defecto Kestrel elegirá un puerto; para fijar el puerto usar `--urls`):

```powershell
dotnet run --urls "http://localhost:5000"
```

- **Probar el endpoint**:

Usando `curl`:

```bash
curl -X POST http://localhost:5000/echo -H "Content-Type: application/json" -d "{\"mensaje\":\"hola\"}"
```

Usando PowerShell:

```powershell
Invoke-RestMethod -Uri "http://localhost:5000/echo" -Method Post -Body (@{mensaje='hola'} | ConvertTo-Json) -ContentType "application/json"
```

Respuesta esperada (JSON):

```json
{"mensaje":"hola hola"}
```

Swagger UI:

 - Accede a `http://localhost:5000/swagger` después de ejecutar la API.

Configurar puerto desde `appsettings.json`:

 - Se ha actualizado `appsettings.json` en este proyecto; ahora la clave `AppSettings:Url` puede apuntar a `http://localhost:5009` según la configuración que hayas aplicado.

 - Para cambiar el puerto edita `appsettings.json`, por ejemplo:

```json
{
	"AppSettings": {
		"Url": "http://localhost:5009"
	}
}
```

 - Luego ejecuta la API y escuchará en la URL/puerto configurado:

```powershell
dotnet run
```


Docker
 - Construir la imagen Docker (desde la raíz del proyecto):

```powershell
docker build -t apiaks-netcore:latest .
```

 - Ejecutar el contenedor (ejemplo usado en este repositorio):

```powershell
docker run -d --name apiaks -p 5009:5000 apiaks-netcore:latest
```

 - Nota: este comando asume que `appsettings.json` dentro de la imagen está configurado para usar `http://localhost:5009` o que la aplicación está enlazada a `0.0.0.0` en el puerto del contenedor. Si la aplicación no responde desde el host, ejecuta con una variable de entorno que fuerce el binding a todas las interfaces:

```powershell
docker run -d --name apiaks -p 5009:5000 -e "AppSettings__Url=http://*:5000" apiaks-netcore:latest
```

Nota: el `Dockerfile` usa un multi-stage build para compilar con el SDK y publicar sobre la imagen de runtime.

# apiAKS_NetCore
# En resumen, estos son los comandos para crear la imagen y el contenedor de apiAKS_NetCore
docker build -t apiaks-netcore:latest .

docker run -d --name apiaks -p 5009:5000 apiaks-netcore:latest