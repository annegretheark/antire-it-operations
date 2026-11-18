terraform {
  required_version = ">= 1.9"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.14"
    }
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.10"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.9"
    }
    time = {
      source  = "hashicorp/time"
      version = "~> 0.14"
    }
  }

  # State i Azure Storage med Entra-innlogging (ingen lagringsnøkler).
  # Verdiene per miljø ligger i backend-<miljø>.hcl:
  #   terraform init -backend-config=backend-dev.hcl
  backend "azurerm" {
    use_azuread_auth = true
  }
}

provider "azurerm" {
  subscription_id     = var.subscription_id
  storage_use_azuread = true

  features {
    key_vault {
      # Dev: en slettet nøkkelhvelv tømmes ved terraform destroy
      purge_soft_delete_on_destroy = var.environment != "prod"
    }
  }
}

provider "azuread" {
  tenant_id = var.tenant_id
}
