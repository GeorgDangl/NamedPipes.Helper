pipeline {
    agent {
        node {
            label 'master'
        }
    }
    environment {
        KeyVaultBaseUrl = credentials('AzureCiKeyVaultBaseUrl')
        KeyVaultClientId = credentials('AzureCiKeyVaultClientId')
        KeyVaultClientSecret = credentials('AzureCiKeyVaultClientSecret')
        KeyVaultTenantId = credentials('AzureKeyVaultTenantId')
    }
    stages {
        stage ('Deploy') {
            steps {
                powershell './build.ps1 UploadDocumentation+PublishGitHubRelease'
            }
        }
    }
    post {
        always {
            step([$class: 'Mailer',
                notifyEveryUnstableBuild: true,
                recipients: [
                    emailextrecipients([
                        [$class: 'CulpritsRecipientProvider'],
                        [$class: 'RequesterRecipientProvider']
                        ])
                    ].join(' '),
                sendToIndividuals: true])
            cleanWs()
        }
    }
}