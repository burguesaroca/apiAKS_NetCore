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

 - El proyecto lee `AppSettings:Url` en `appsettings.json`. Por defecto está en `http://localhost:5000`.
 - Para cambiar el puerto edita `appsettings.json`, por ejemplo:

```json
{
	"AppSettings": {
		"Url": "http://localhost:5050"
	}
}
```

 - Luego ejecuta la API y escuchará en el puerto configurado:

```powershell
dotnet run
```

Docker
 - Construir la imagen Docker (desde la raíz del proyecto):

```powershell
docker build -t apiaks-netcore:latest .
```

 - Ejecutar el contenedor exponiendo el puerto 5000 localmente:

```powershell
docker run --rm -p 5000:5000 apiaks-netcore:latest
```

 - Para usar otro puerto local o cambiar la URL que la aplicación escucha, exporta la variable `ASPNETCORE_URLS` al ejecutar:

```powershell
docker run --rm -p 5050:5050 -e ASPNETCORE_URLS="http://*:5050" apiaks-netcore:latest
```

Nota: el `Dockerfile` usa un multi-stage build para compilar con el SDK y publicar sobre la imagen de runtime.

# apiAKS_NetCore