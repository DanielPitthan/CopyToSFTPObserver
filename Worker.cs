using Infrastructure;
using Infrastructure.Email;
using Infrastructure.Extensions;
using Infrastructure.Factorys;
using Microsoft.Extensions.Options;
using Models.Enums;
using Models.MappingTasks;

namespace CopyToSFTPObserver
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly AppSettings _appSettings;
        private readonly AppTaskMapperConfigurator _appTaskMapperConfigurator;
        private int _executionCount = 0;

        public Worker(ILogger<Worker> logger,
            IOptions<AppSettings> appSettings,
            AppTaskMapperConfigurator appTaskMapperConfigurator)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appSettings = appSettings?.Value ?? throw new ArgumentNullException(nameof(appSettings));
            _appTaskMapperConfigurator = appTaskMapperConfigurator ?? throw new ArgumentNullException(nameof(appTaskMapperConfigurator));
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Servi�o em execu��o: {time}", DateTimeOffset.Now);
            return base.StartAsync(cancellationToken);
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Servi�o em parada: {time}", DateTimeOffset.Now);
            return base.StopAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation("Processamento em execu��o: {time}", DateTimeOffset.Now);

                //Faz o mapeamento antes de entrar no loop, para n�o ter que mapear constantemente 
                _logger.LogInformation("Mapeando a��es a serem executadas...");
                AppTask? appTask = _appTaskMapperConfigurator.MapAppTask();
                _logger.LogInformation($"A��es mapeadas: {appTask?.FolderMaps.Count() ?? 0}");
                
                if (appTask == null)
                {
                    _logger.LogWarning("Processo finalizado sem a��es a serem executadas. Causa: Nenhuma a��o mapeada ou erro ao mapear a��es.");
                    await this.StopAsync(stoppingToken);
                    return; // Importante: sair do m�todo ap�s chamar StopAsync
                }

                if (appTask.FolderMaps == null || !appTask.FolderMaps.Any())
                {
                    _logger.LogWarning("Nenhuma pasta foi mapeada para processamento.");
                    await this.StopAsync(stoppingToken);
                    return;
                }

                _logger.LogInformation("Executando: {name} - Version: {version}", appTask.Name ?? "N/A", appTask.Version ?? "N/A");

                //Executa o processamento das pastas
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await ProcessFolders(appTask.FolderMaps, stoppingToken);
                        _logger.LogInformation("Todas as tarefas foram executadas.");
                        await ProximaExecucao(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("Processamento cancelado pelo token de cancelamento.");
                        break;
                    }
                    catch (DirectoryNotFoundException ex)
                    {
                        _logger.LogError("Diret�rio n�o encontrado: {message}", ex.Message);
                        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); // Aguarda antes de tentar novamente
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        _logger.LogError("Acesso negado: {message}", ex.Message);
                        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    }
                    catch (IOException ex)
                    {
                        _logger.LogError("Erro de I/O: {message}", ex.Message);
                        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Erro inesperado durante o processamento: {message}", ex.Message);
                        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); // Delay maior para erros inesperados
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Erro cr�tico no servi�o: {message}", ex.Message);
                throw; // Re-throw para garantir que o servi�o seja parado
            }
        }

        private async Task ProcessFolders(IEnumerable<FolderMap> folderMaps, CancellationToken stoppingToken)
        {
            foreach (var folderMap in folderMaps)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;

                try
                {
                    await ProcessSingleFolder(folderMap, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro ao processar pasta {folderName}: {message}", 
                        folderMap?.Name ?? "N/A", ex.Message);
                    // Continue processando outras pastas mesmo se uma falhar
                }
            }
        }

        private async Task ProcessSingleFolder(FolderMap folderMap, CancellationToken stoppingToken)
        {
            if (folderMap == null)
            {
                _logger.LogWarning("FolderMap � null, pulando processamento.");
                return;
            }

            _logger.LogInformation("Processando pasta: {folderName}", folderMap.Name ?? "N/A");
            _logger.LogInformation("Caminho da pasta: {folderPath}", folderMap.FolderPath ?? "N/A");
            _logger.LogInformation("Destino SFTP: {sftpPathDestination}", folderMap.SFTPPathDestination ?? "N/A");
            _logger.LogInformation("Destino no caso de erro: {processedFilesOnError}", folderMap.ProcessedFilesOnError ?? "N/A");
            _logger.LogInformation("Destino no caso de sucesso: {processedFilesOnSuccess}", folderMap.ProcessedFilesOnSuccess ?? "N/A");
            _logger.LogInformation("Notifica��o por e-mail: {emailNotify}", folderMap.EmailNotify);

            if (folderMap.TasksMaps == null || !folderMap.TasksMaps.Any())
            {
                _logger.LogWarning("Nenhuma tarefa mapeada para a pasta {folderName}", folderMap.Name);
                return;
            }

            List<TaskActions> tasksActions = new List<TaskActions>();

            //Gera as a��es a serem executadas para cada pasta
            foreach (var taskMap in folderMap.TasksMaps.OrderBy(t => t.Order))
            {
                if (stoppingToken.IsCancellationRequested)
                    break;

                try
                {
                    var variableName = taskMap.Name?.ExtractVariable();
                    var value = variableName?.GetValue(folderMap);
                    var taskName = variableName != null && !string.IsNullOrEmpty(taskMap.Name) 
                        ? taskMap.Name.Replace("@" + variableName, value ?? string.Empty) 
                        : taskMap.Name ?? "N/A";
                    
                    _logger.LogInformation("[{order}] Criando tarefa: {taskName}", taskMap.Order, taskName);

                    var taskAction = TaskActionFactory.CreateTaskAction(taskMap.Task, folderMap, taskName);
                    if (taskAction != null)
                    {
                        tasksActions.Add(taskAction);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro ao criar tarefa {taskName}: {message}", taskMap.Name ?? "N/A", ex.Message);
                }
            }

            await ExecuteTasks(tasksActions, folderMap, stoppingToken);
        }

        private async Task ExecuteTasks(List<TaskActions> tasksActions, FolderMap folderMap, CancellationToken stoppingToken)
        {
            bool goToNextTask = true;
            string emailText = @"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Document</title>
</head>
<body>";
            //Executa as tarefas 
            foreach (TaskActions task in tasksActions)
            {
                if (stoppingToken.IsCancellationRequested || !goToNextTask)
                    break;

                if (task == null)
                {
                    _logger.LogWarning("TaskAction � null, pulando execu��o.");
                    continue;
                }

                _logger.LogInformation("Executando tarefa: {taskname}", task.Name ?? "N/A");

                try
                {
                    //Faz a c�pia para o SFTP
                    if (task.Action == TypeOfTasks.copy && goToNextTask)
                    {
                        var result = task.ExecuteCopy();
                        goToNextTask = result?.Success ?? false;
                        emailText += result?.Message + "<br />" ?? "Resultado da c�pia n�o dispon�vel  <br />";                      
                        _logger.LogInformation(result?.Message ?? "Resultado da c�pia n�o dispon�vel");
                    }
                    
                    if (!goToNextTask)
                    {
                        await HandleTaskFailure(task, folderMap);
                        continue;
                    }

                    //Faz a verifica��o de arquivos no SFTP
                    if (task.Action == TypeOfTasks.check && goToNextTask)
                    {
                        var result = task.Check();
                        goToNextTask = result?.Success ?? false;
                        emailText += result?.Message + "<br />" ?? "Resultado da verifica��o n�o dispon�vel <br />";
                        _logger.LogInformation(result?.Message ?? "Resultado da verifica��o n�o dispon�vel");
                    }
                    
                    if (!goToNextTask)
                    {
                        await HandleTaskFailure(task, folderMap);
                        continue;
                    }

                    //Move os arquivos para a pasta de sucesso
                    if (task.Action == TypeOfTasks.move && goToNextTask)
                    {
                        var result = task.Move();
                        goToNextTask = result?.Success ?? false;
                        emailText += result?.Message + "<br />" ?? "Resultado da movimenta��o n�o dispon�vel  <br />";
                        _logger.LogInformation(result?.Message ?? "Resultado da movimenta��o n�o dispon�vel");
                    }
                    
                    if (!goToNextTask)
                    {
                        await HandleTaskFailure(task, folderMap);
                        continue;
                    }

                    //Exclui os arquivos que foram copiados
                    if (task.Action == TypeOfTasks.delete && goToNextTask)
                    {
                        var result = DeleteFileFactory.Execute(task, folderMap);
                        goToNextTask = result?.Success ?? false;
                        emailText += result?.Message + "<br />" ?? "Resultado da exclus�o n�o dispon�vel <br />";
                        _logger.LogInformation(result?.Message ?? "Resultado da exclus�o n�o dispon�vel");
                    }
                    
                    if (!goToNextTask)
                    {
                        await HandleTaskFailure(task, folderMap);
                        continue;
                    }


                    if (task.Action == TypeOfTasks.notify && goToNextTask)
                    {
                        emailText += @"</body>
                                        </html>";
                        Email email = new Email(folderMap.EmailNotify, $"Notifica��o de tarefa conclu�da: {folderMap.Name}",
                            $"Log de execu��o da tarefa\r\n {emailText}");
                        email.Send();
                        _logger.LogInformation($"Notifica��o enviada para: {folderMap.EmailNotify}");
                    }

                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro inesperado ao executar tarefa {taskName}: {message}", task.Name ?? "N/A", ex.Message);
                    goToNextTask = false;
                    await HandleTaskFailure(task, folderMap);
                }
            }
        }

        private async Task HandleTaskFailure(TaskActions task, FolderMap folderMap)
        {
            try
            {
                var result = task.MoveToErrorFolder(folderMap?.ProcessedFilesOnError ?? string.Empty);
                _logger.LogError("A tarefa {taskName} falhou, n�o prosseguindo para a pr�xima tarefa.", task?.Action.ToString() ?? "N/A");
                _logger.LogError(result?.Message ?? "Erro ao mover arquivos para pasta de erro");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao mover arquivos para pasta de erro: {message}", ex.Message);
            }
            
            // Pequeno delay para evitar satura��o em caso de erros cont�nuos
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        private async Task ProximaExecucao(CancellationToken stoppingToken)
        {
            var proximaExecucao = DateTime.Now.AddMilliseconds(_appSettings.IntervaloEntreExecucoes);
            _logger.LogInformation("Pr�xima execu��o em: {proximaExecucao}", proximaExecucao);
            
            try
            {
                await Task.Delay(_appSettings.IntervaloEntreExecucoes, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Delay cancelado pelo token de cancelamento.");
            }
        }
    }
}
