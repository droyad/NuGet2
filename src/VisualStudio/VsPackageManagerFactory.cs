using System;
using System.ComponentModel.Composition;
using System.Linq;
using EnvDTE;
using Microsoft.VisualStudio.Shell.Interop;

namespace NuGet.VisualStudio
{
    [PartCreationPolicy(CreationPolicy.Shared)]
    [Export(typeof(IVsPackageManagerFactory))]
    public class VsPackageManagerFactory : IVsPackageManagerFactory
    {
        private readonly IPackageRepositoryFactory _repositoryFactory;
        private readonly ISolutionManager _solutionManager;
        private readonly IFileSystemProvider _fileSystemProvider;
        private readonly IRepositorySettings _repositorySettings;
        private readonly IVsPackageSourceProvider _packageSourceProvider;
        private readonly VsPackageInstallerEvents _packageEvents;
        private readonly IPackageRepository _activePackageSourceRepository;
        private readonly IVsFrameworkMultiTargeting _frameworkMultiTargeting;
        private RepositoryInfo _repositoryInfo;

        [ImportingConstructor]
        public VsPackageManagerFactory(ISolutionManager solutionManager,
                                       IPackageRepositoryFactory repositoryFactory,
                                       IVsPackageSourceProvider packageSourceProvider,
                                       IFileSystemProvider fileSystemProvider,
                                       IRepositorySettings repositorySettings,
                                       VsPackageInstallerEvents packageEvents,
                                       IPackageRepository activePackageSourceRepository) :
            this(solutionManager, 
                 repositoryFactory, 
                 packageSourceProvider, 
                 fileSystemProvider, 
                 repositorySettings, 
                 packageEvents,
                 activePackageSourceRepository,
                 ServiceLocator.GetGlobalService<SVsFrameworkMultiTargeting, IVsFrameworkMultiTargeting>())
        {
        }

        public VsPackageManagerFactory(ISolutionManager solutionManager,
                                       IPackageRepositoryFactory repositoryFactory,
                                       IVsPackageSourceProvider packageSourceProvider,
                                       IFileSystemProvider fileSystemProvider,
                                       IRepositorySettings repositorySettings,
                                       VsPackageInstallerEvents packageEvents,
                                       IPackageRepository activePackageSourceRepository,
                                       IVsFrameworkMultiTargeting frameworkMultiTargeting)
        {
            if (solutionManager == null)
            {
                throw new ArgumentNullException("solutionManager");
            }
            if (repositoryFactory == null)
            {
                throw new ArgumentNullException("repositoryFactory");
            }
            if (packageSourceProvider == null)
            {
                throw new ArgumentNullException("packageSourceProvider");
            }
            if (fileSystemProvider == null)
            {
                throw new ArgumentNullException("fileSystemProvider");
            }
            if (repositorySettings == null)
            {
                throw new ArgumentNullException("repositorySettings");
            }
            if (packageEvents == null)
            {
                throw new ArgumentNullException("packageEvents");
            }
            if (activePackageSourceRepository == null)
            {
                throw new ArgumentNullException("activePackageSourceRepository");
            }

            _fileSystemProvider = fileSystemProvider;
            _repositorySettings = repositorySettings;
            _solutionManager = solutionManager;
            _repositoryFactory = repositoryFactory;
            _packageSourceProvider = packageSourceProvider;
            _packageEvents = packageEvents;
            _activePackageSourceRepository = activePackageSourceRepository;
            _frameworkMultiTargeting = frameworkMultiTargeting;

            _solutionManager.SolutionClosing += (sender, e) =>
            {
                _repositoryInfo = null;
            };
        }

        /// <summary>
        /// Creates an VsPackageManagerInstance that uses the Active Repository (the repository selected in the console drop down) and uses a fallback repository for dependencies.
        /// </summary>
        public IVsPackageManager CreatePackageManager()
        {
            return CreatePackageManager(_activePackageSourceRepository, useFallbackForDependencies: true);
        }

        public IVsPackageManager CreatePackageManager(IPackageRepository repository, bool useFallbackForDependencies)
        {
            if (useFallbackForDependencies)
            {
                repository = CreateFallbackRepository(repository);
            }
            RepositoryInfo info = GetRepositoryInfo();
            return new VsPackageManager(_solutionManager,
                                        repository,
                                        _fileSystemProvider,
                                        info.FileSystem,
                                        info.Repository,
                                        // We ensure DeleteOnRestartManager is initialized with a PhysicalFileSystem so the
                                        // .deleteme marker files that get created don't get checked into version control
                                        new DeleteOnRestartManager(() => new PhysicalFileSystem(info.FileSystem.Root)),
                                        _packageEvents,
                                        _frameworkMultiTargeting);
        }

        public IVsPackageManager CreatePackageManagerWithAllPackageSources()
        {
            return CreatePackageManagerWithAllPackageSources(_activePackageSourceRepository);
        }

        internal IVsPackageManager CreatePackageManagerWithAllPackageSources(IPackageRepository repository)
        {
            if (IsAggregateRepository(repository))
            {
               return  CreatePackageManager(repository, false);
            }

            return CreatePackageManager(CreatePackageRestoreRepository(repository), useFallbackForDependencies: false);
        }

        /// <summary>
        /// Creates a FallbackRepository with an aggregate repository that also contains the primaryRepository.
        /// </summary>
        internal IPackageRepository CreateFallbackRepository(IPackageRepository primaryRepository)
        {
            if (IsAggregateRepository(primaryRepository))
            {
                // If we're using the aggregate repository, we don't need to create a fall back repo.
                return primaryRepository;
            }

            var aggregateRepository = _packageSourceProvider.GetAggregate(_repositoryFactory, ignoreFailingRepositories: true);
            aggregateRepository.ResolveDependenciesVertically = true;
            return new FallbackRepository(primaryRepository, aggregateRepository);
        }

        internal IPackageRepository CreatePackageRestoreRepository(IPackageRepository primaryRepository)
        {
            var nonActivePackageSources = _packageSourceProvider.GetEnabledPackageSources()
                                          .Where(s => !s.Source.Equals(primaryRepository.Source, StringComparison.OrdinalIgnoreCase))
                                          .Select(s => s.Source)
                                          .ToList();

            if (nonActivePackageSources.IsEmpty())
            {
                return primaryRepository;
            }

            var aggregateRepository = nonActivePackageSources.Count > 1 ?
                _packageSourceProvider.GetAggregate(_repositoryFactory, ignoreFailingRepositories: true, feeds: nonActivePackageSources)
                : _repositoryFactory.CreateRepository(nonActivePackageSources[0]);

            return new PackageRestoreRepository(primaryRepository, aggregateRepository);
        }

        private static bool IsAggregateRepository(IPackageRepository repository)
        {
            if (repository is AggregateRepository)
            {
                // This test should be ok as long as any aggregate repository that we encounter here means the true Aggregate repository of all repositories in the package source
                // Since the repository created here comes from the UI, this holds true.
                return true;
            }
            var vsPackageSourceRepository = repository as VsPackageSourceRepository;
            if (vsPackageSourceRepository != null)
            {
                return IsAggregateRepository(vsPackageSourceRepository.GetActiveRepository());
            }
            return false;
        }

        private RepositoryInfo GetRepositoryInfo()
        {
            // Update the path if it needs updating
            string path = _repositorySettings.RepositoryPath;
            string configFolderPath = _repositorySettings.ConfigFolderPath;

            if (_repositoryInfo == null || 
                !_repositoryInfo.Path.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                !_repositoryInfo.ConfigFolderPath.Equals(configFolderPath, StringComparison.OrdinalIgnoreCase) ||
                _solutionManager.IsSourceControlBound != _repositoryInfo.IsSourceControlBound)
            {
                IFileSystem fileSystem = _fileSystemProvider.GetFileSystem(path);
                IFileSystem configSettingsFileSystem = GetConfigSettingsFileSystem(configFolderPath);
                // this file system is used to access the repositories.config file. We want to use Source Control-bound 
                // file system to access it even if the 'disableSourceControlIntegration' setting is set.
                IFileSystem storeFileSystem = _fileSystemProvider.GetFileSystem(path, ignoreSourceControlSetting: true);
                
                ISharedPackageRepository repository = new SharedPackageRepository(
                    new DefaultPackagePathResolver(fileSystem), 
                    fileSystem, 
                    storeFileSystem, 
                    configSettingsFileSystem);

                _repositoryInfo = new RepositoryInfo(path, configFolderPath, fileSystem, repository);
            }

            return _repositoryInfo;
        }

        protected internal virtual IFileSystem GetConfigSettingsFileSystem(string configFolderPath)
        {
            return new SolutionFolderFileSystem(ServiceLocator.GetInstance<DTE>().Solution, VsConstants.NuGetSolutionSettingsFolder, configFolderPath);
        }

        private class RepositoryInfo
        {
            public RepositoryInfo(string path, string configFolderPath, IFileSystem fileSystem, ISharedPackageRepository repository)
            {
                Path = path;
                FileSystem = fileSystem;
                Repository = repository;
                ConfigFolderPath = configFolderPath;
            }

            public bool IsSourceControlBound
            {
                get
                {
                    return FileSystem is ISourceControlFileSystem;
                }
            }

            public IFileSystem FileSystem { get; private set; }
            public string Path { get; private set; }
            public string ConfigFolderPath { get; private set; }
            public ISharedPackageRepository Repository { get; private set; }
        }
    }
}