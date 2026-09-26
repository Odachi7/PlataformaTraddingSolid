using System.Text.RegularExpressions;

namespace Trading.Application.Accounts.Signup;

public class SignupValidator 
{
	public string? Validate(SignupCommand command) 
	{
		if (command.Name is null || command.Name == "")
		{
			return "Nome é obrigatorio!";
		}

		var names = command.Name.Trim().Split(' ',StringSplitOptions.RemoveEmptyEntries);

		if(names.Length < 2) 
		{
			return "Nome precisa conter Nome e Sobrenome!";
		}

		if (!Regex.IsMatch(command.Email ?? "",@"^[^\s@]+@[^\s@]+\.[^\s@]+$"))
		{
			return "Email invalido!";
		}

		if (!ValidCpf(command.Document)) 
		{
			return "Cpf precisa ser válido!";
		}

		if (string.IsNullOrEmpty(command.Password) ||
			command.Password.Length < 8 ||
			!command.Password.Any(char.IsLower) ||
			!command.Password.Any(char.IsUpper) ||
			!command.Password.Any(char.IsDigit))
		{
			return "Senha precisa conter mais de 8 caracteres, contendo Letra Maiuscula, Minuscula e Numeros.";
		}

		return null;
	}


	private static bool ValidCpf(string document)
	{
		var cpf = new string(
			(document ?? "")
			.Where(char.IsDigit)
			.ToArray());

		if (cpf.Length != 11)
			return false;

		if (cpf.Distinct().Count() == 1)
			return false;

		var numbers =
			cpf.Select(c => c - '0').ToArray();

		var sum = 0;

		for (var i = 0; i < 9; i++)
			sum += numbers[i] * (10 - i);

		var remainder = sum % 11;

		var digit1 =
			remainder < 2 ? 0 : 11 - remainder;

		if (numbers[9] != digit1)
			return false;

		sum = 0;

		for (var i = 0; i < 10; i++)
			sum += numbers[i] * (11 - i);

		remainder = sum % 11;

		var digit2 =
			remainder < 2 ? 0 : 11 - remainder;

		return numbers[10] == digit2;
	}
}