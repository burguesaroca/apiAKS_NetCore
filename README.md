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

# apiAKS_NetCore